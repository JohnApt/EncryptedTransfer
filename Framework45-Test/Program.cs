﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Net.Sockets;
using Gerk.Crypto.EncyrptedTransfer;
using System.IO;

namespace ConsoleApp1
{
	public static class Program
	{
		public static int startbreaking = 0;

		public static string reciver(Stream stream, RSAParameters local, RSAParameters remote, string send)
		{
			using (var rsa = new RSACryptoServiceProvider())
			{
				rsa.ImportParameters(local);
				using (var tunnel = Tunnel.CreateResponder(stream, new RSAParameters[] { remote }, rsa, out var err))
				{
					if (err != TunnelCreationError.NoError)
						throw new Exception(err.ToString());
					startbreaking++;
					using (var reader = new BinaryReader(tunnel))
					using (var writer = new BinaryWriter(tunnel))
					{
						writer.Write(send);
						tunnel.FlushWriter();
						var line = reader.ReadString();
						return line;
					}
				}
			}
		}

		public static string sender(Stream stream, RSAParameters local, RSAParameters remote, string send)
		{
			using (var rsa = new RSACryptoServiceProvider())
			{
				rsa.ImportParameters(local);
				using (var tunnel = Tunnel.CreateInitiator(stream, new RSAParameters[] { remote }, rsa, out var err))
				{
					if (err != TunnelCreationError.NoError)
						throw new Exception(err.ToString());
					startbreaking++;
					using (var reader = new BinaryReader(tunnel))
					using (var writer = new BinaryWriter(tunnel))
					{
						writer.Write(send);
						tunnel.FlushWriter();
						var line = reader.ReadString();
						return line;
					}
				}
			}
		}

		public static async Task Main()
		{
			TcpListener server = new TcpListener(System.Net.IPAddress.Loopback, 0);
			server.Start();
			TcpClient client = new TcpClient();
			client.Connect((server.LocalEndpoint as System.Net.IPEndPoint));

			using (var c = new RSACryptoServiceProvider())
			using (var d = new RSACryptoServiceProvider())
			{

				const string msg = "Hello world!";
				const string response = "Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt ut labore et dolore magna aliqua. Ut enim ad minim veniam, quis nostrud exercitation ullamco laboris nisi ut aliquip ex ea commodo consequat. Duis aute irure dolor in reprehenderit in voluptate velit esse cillum dolore eu fugiat nulla pariatur. Excepteur sint occaecat cupidatat non proident, sunt in culpa qui officia deserunt mollit anim id est laborum.";



				var sendTask = Task.Run(() => sender(client.GetStream(), c.ExportParameters(true), d.ExportParameters(false), msg));
				var reciveTask = Task.Run(() => reciver(server.AcceptTcpClient().GetStream(), d.ExportParameters(true), c.ExportParameters(false), response));
				await Task.WhenAll(sendTask, reciveTask);
				Console.WriteLine(await sendTask == response);
				Console.WriteLine(await reciveTask == msg);
			}
		}
	}
}