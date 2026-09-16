using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace BPRE.Multiplayer
{
	/// <summary>
	/// Accepts TCP clients on a background thread. New connections are handed to the
	/// main thread through Update().
	/// </summary>
	public sealed class NetServer
	{
		private TcpListener m_listener;

		private Thread m_acceptThread;

		private volatile bool m_running;

		private readonly ConcurrentQueue<TcpClient> m_pending = new ConcurrentQueue<TcpClient>();

		private readonly List<NetConnection> m_connections = new List<NetConnection>();

		public int Port { get; private set; }

		public bool IsRunning => m_running;

		public IReadOnlyList<NetConnection> Connections => m_connections;

		/// <summary>Throws SocketException when the port cannot be bound.</summary>
		public void Start(int port)
		{
			Stop();
			m_listener = new TcpListener(IPAddress.Any, port);
			m_listener.Start();
			Port = port;
			m_running = true;
			m_acceptThread = new Thread(AcceptLoop)
			{
				IsBackground = true,
				Name = "BPRE.Net.Accept"
			};
			m_acceptThread.Start();
		}

		/// <summary>Drains newly accepted sockets. Returns the connections created this frame.</summary>
		public List<NetConnection> Update()
		{
			List<NetConnection> created = null;
			while (m_pending.TryDequeue(out TcpClient client))
			{
				if (!m_running)
				{
					try
					{
						client.Close();
					}
					catch
					{
					}
					continue;
				}
				NetConnection connection;
				try
				{
					connection = new NetConnection(client);
				}
				catch (Exception)
				{
					continue;
				}
				m_connections.Add(connection);
				created ??= new List<NetConnection>();
				created.Add(connection);
			}
			return created ?? new List<NetConnection>(0);
		}

		public void Broadcast(byte[] message, NetConnection except = null)
		{
			for (int i = 0; i < m_connections.Count; i++)
			{
				NetConnection connection = m_connections[i];
				if (connection != except && !connection.IsClosed)
				{
					connection.Send(message);
				}
			}
		}

		public void Remove(NetConnection connection, string reason)
		{
			if (connection == null)
			{
				return;
			}
			connection.Close(reason);
			m_connections.Remove(connection);
		}

		public void Stop()
		{
			m_running = false;
			if (m_listener != null)
			{
				try
				{
					m_listener.Stop();
				}
				catch
				{
				}
				m_listener = null;
			}
			foreach (NetConnection connection in m_connections)
			{
				connection.Close("server stopped");
			}
			m_connections.Clear();
			while (m_pending.TryDequeue(out TcpClient client))
			{
				try
				{
					client.Close();
				}
				catch
				{
				}
			}
		}

		private void AcceptLoop()
		{
			TcpListener listener = m_listener;
			try
			{
				while (m_running && listener != null)
				{
					TcpClient client = listener.AcceptTcpClient();
					m_pending.Enqueue(client);
				}
			}
			catch (Exception)
			{
				// Listener was stopped or failed; the session notices via IsRunning.
			}
		}

		/// <summary>Best-effort list of local IPv4 addresses to show the host.</summary>
		public static List<string> GetLocalAddresses()
		{
			List<string> result = new List<string>();
			try
			{
				foreach (IPAddress address in Dns.GetHostAddresses(Dns.GetHostName()))
				{
					if (address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
					{
						result.Add(address.ToString());
					}
				}
			}
			catch
			{
			}
			return result;
		}
	}
}
