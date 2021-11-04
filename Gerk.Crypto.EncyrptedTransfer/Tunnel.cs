﻿using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Gerk.BinaryExtension;

namespace Gerk.Crypto.EncyrptedTransfer
{
	public enum TunnelCreationError
	{
		NoError = 0,
		RemoteDoesNotHaveValidPublicKey,
		RemoteFailedToVierfyItself,
	}


	public class Tunnel : Stream
	{
		/// <summary>
		/// Size of the challenge message in bytes to initiate connection.
		/// </summary>
		private const int CHALLANGE_SIZE = 256;
		private const bool USE_OAEP_PADDING = true;
		private const int AES_KEY_LENGTH = 256;

		private CryptoStream readStream;
		private CryptoStream writeStream;
		private Aes sharedKey = null;
		private Stream underlyingStream;
		private ulong bytesRead = 0;
		private uint blockSize;
		private ulong bytesWritten = 0;
		public uint BlockSize => blockSize;

#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
		private bool leaveOpen;
#endif
		/// <summary>
		/// The public key for the other end of the connection. Can be used as an identity.
		/// </summary>
		public RSAParameters remotePublicKey { private set; get; }

#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
		private Tunnel(Stream stream, bool leaveOpen = false)
		{
			this.underlyingStream = stream;
			this.leaveOpen = leaveOpen;
		}
#else
		private Tunnel(Stream stream)
		{
			this.underlyingStream = stream;
		}
#endif

		private void InitCryptoStreams()
		{
			var dec = sharedKey.CreateDecryptor();
			var enc = sharedKey.CreateEncryptor();
			blockSize = (uint)enc.InputBlockSize;
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
			readStream = new CryptoStream(underlyingStream, dec, CryptoStreamMode.Read, leaveOpen);
			writeStream = new CryptoStream(underlyingStream, enc, CryptoStreamMode.Write, leaveOpen);
#else
			readStream = new CryptoStream(underlyingStream, dec, CryptoStreamMode.Read);
			writeStream = new CryptoStream(underlyingStream, enc, CryptoStreamMode.Write);
#endif
		}

		private static Aes ReadAesKey(BinaryReader bw, RSACryptoServiceProvider rsa)
		{
			var aes = Aes.Create();
			aes.Padding = PaddingMode.None;
			aes.Mode = CipherMode.ECB;
			using (var memStream = new MemoryStream(rsa.Decrypt(bw.ReadBinaryData(), USE_OAEP_PADDING)))
			using (var memReader = new BinaryReader(memStream))
			{
				aes.Key = memReader.ReadBinaryData();
				aes.IV = memReader.ReadBinaryData();
			}
			return aes;
		}

		private static void WriteAesKey(Aes aes, BinaryWriter bw, RSACryptoServiceProvider rsa)
		{
			using (var memStream = new MemoryStream())
			using (var memWriter = new BinaryWriter(memStream))
			{
				memWriter.WriteBinaryData(aes.Key);
				memWriter.WriteBinaryData(aes.IV);
				bw.WriteBinaryData(rsa.Encrypt(memStream.ToArray(), USE_OAEP_PADDING));
			}
		}

		public static Tunnel CreateInitiator(Stream stream, IEnumerable<RSAParameters> remotePublicKeys, RSACryptoServiceProvider localPrivateKey, out TunnelCreationError error
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
			, bool leaveOpen = false
#endif
		)
		{
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
			Tunnel output = new Tunnel(stream, leaveOpen);
#else
			Tunnel output = new Tunnel(stream);
#endif
			try
			{
				using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
				using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
				{
					// write some metadata

					// write public key
					writer.WriteBinaryData(localPrivateKey.ExportCspBlob(false));

					// write challenge
					var challengeMessage = new byte[CHALLANGE_SIZE];
					using (var rand = new RNGCryptoServiceProvider())
						rand.GetBytes(challengeMessage);
					writer.Write(challengeMessage);

					// read encrypted AES key
					output.sharedKey = ReadAesKey(reader, localPrivateKey);

					// read remote public key
					using (var remotePublicKey = new RSACryptoServiceProvider())
					{
						remotePublicKey.ImportCspBlob(reader.ReadBinaryData());
						output.remotePublicKey = remotePublicKey.ExportParameters(false);
						if (!remotePublicKeys.Any(x => x.Modulus.SequenceEqual(output.remotePublicKey.Modulus)))
						{
							output.Dispose();
							error = TunnelCreationError.RemoteDoesNotHaveValidPublicKey;
							return null;
						}

						// read challenge signature
						using (var hash = SHA256.Create())
							if (!remotePublicKey.VerifyData(challengeMessage, hash, reader.ReadBinaryData()))
							{
								output.Dispose();
								error = TunnelCreationError.RemoteFailedToVierfyItself;
								return null;
							}
					}
				}

				output.InitCryptoStreams();
				error = TunnelCreationError.NoError;
				return output;
			}
			catch
			{
				output.Dispose();
				throw;
			}
		}

		public static Tunnel CreateResponder(Stream stream, IEnumerable<RSAParameters> remotePublicKeys, RSACryptoServiceProvider localPrivateKey, out TunnelCreationError error
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
			, bool leaveOpen = false
#endif
			)
		{
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
			Tunnel output = new Tunnel(stream, leaveOpen);
#else
			Tunnel output = new Tunnel(stream);
#endif
			try
			{
				using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
				using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
				{
					// read some metadata

					// read public key
					using (var remotePublicKey = new RSACryptoServiceProvider())
					{
						remotePublicKey.ImportCspBlob(reader.ReadBinaryData());
						output.remotePublicKey = remotePublicKey.ExportParameters(false);
						if (!remotePublicKeys.Any(x => x.Modulus.SequenceEqual(output.remotePublicKey.Modulus)))
						{
							output.Dispose();
							error = TunnelCreationError.RemoteDoesNotHaveValidPublicKey;
							return null;
						}

						// write encrypted AES key
						output.sharedKey = Aes.Create();
						output.sharedKey.Mode = CipherMode.ECB;
						output.sharedKey.Padding = PaddingMode.None;
						output.sharedKey.KeySize = AES_KEY_LENGTH;
						output.sharedKey.GenerateIV();
						WriteAesKey(output.sharedKey, writer, remotePublicKey);
					}

					// read challenge
					byte[] challengeMessage = new byte[CHALLANGE_SIZE];
					reader.Read(challengeMessage, 0, CHALLANGE_SIZE);

					// write local public key
					writer.WriteBinaryData(localPrivateKey.ExportCspBlob(false));

					// write challenge signature
					using (var hash = SHA256.Create())
						writer.WriteBinaryData(localPrivateKey.SignData(challengeMessage, hash));
				}

				output.InitCryptoStreams();
				error = TunnelCreationError.NoError;
				return output;
			}
			catch
			{
				output.Dispose();
				throw;
			}
		}

		public override bool CanRead => true;

		public override bool CanSeek => false;

		public override bool CanWrite => true;

		public override long Length => throw new NotSupportedException();

		public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

		public override void Flush() => underlyingStream.Flush();

		public virtual void FlushWriter()
		{
			int bytesToWrite = (int)(blockSize - (bytesWritten % blockSize));
			//Write(new byte[32], 0, 32);
			if (bytesToWrite != blockSize)
				Write(new byte[bytesToWrite], 0, bytesToWrite);
			//writeStream.FlushFinalBlock();
		}

		public virtual void FlushReader()
		{
			int bytesToRead = (int)(blockSize - bytesRead % blockSize);
			if (bytesToRead != blockSize)
				Write(new byte[bytesToRead], 0, bytesToRead);
		}
#if NET5_0
		public string GetKey()
		{
			byte[] me = new byte[16];
			var hash = SHA256.HashData(sharedKey.Key);
			Buffer.BlockCopy(hash, 0, me, 0, 16);
			return new Guid(me).ToString();
		}
#endif
		public override int Read(byte[] buffer, int offset, int count)
		{
			var read = readStream.Read(buffer, offset, count);
			bytesRead += (ulong)read;
			return read;
		}

		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

		public override void SetLength(long value) => throw new NotSupportedException();

		public override void Write(byte[] buffer, int offset, int count)
		{
			bytesWritten += (ulong)count;
			writeStream.Write(buffer, offset, count);
		}

#if NETCOREAPP3_0_OR_GREATER || NETSTANDARD2_1_OR_GREATER
		public override async ValueTask DisposeAsync()
		{
			sharedKey.Dispose();
			var a = writeStream.DisposeAsync();
			var b = readStream.DisposeAsync();
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
			if (!leaveOpen)
				await underlyingStream.DisposeAsync();
#endif
			await a;
			await b;
		}
#endif

		public override void Close()
		{
			sharedKey.Dispose();
			writeStream?.Close();
			readStream?.Close();
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
			if (!leaveOpen)
#endif
				underlyingStream?.Close();
		}
	}
}
