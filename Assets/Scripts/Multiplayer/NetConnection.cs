using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Threading;

namespace BPRE.Multiplayer
{
	/// <summary>
	/// One TCP connection with a dedicated receive thread and send thread.
	/// Incoming messages are queued and must be drained from the main thread via TryReceive.
	/// </summary>
	public sealed class NetConnection
	{
		private readonly TcpClient m_client;

		private readonly NetworkStream m_stream;

		private readonly ConcurrentQueue<byte[]> m_incoming = new ConcurrentQueue<byte[]>();

		private readonly BlockingCollection<byte[]> m_outgoing = new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>());

		private readonly Thread m_receiveThread;

		private readonly Thread m_sendThread;

		private int m_closed;

		private string m_closeReason;

		/// <summary>Assigned by the session once the handshake completed. 0 = not yet identified.</summary>
		public int PlayerId { get; set; }

		public string RemoteAddress { get; }

		public bool IsClosed => m_closed != 0;

		public string CloseReason => m_closeReason;

		/// <summary>Main-thread bookkeeping for keepalive handling.</summary>
		public float LastActivityTime { get; set; }

		public float LastPingSentTime { get; set; }

		public NetConnection(TcpClient client)
		{
			m_client = client;
			try
			{
				m_client.NoDelay = true;
				RemoteAddress = m_client.Client.RemoteEndPoint?.ToString() ?? "?";
			}
			catch
			{
				RemoteAddress = "?";
			}
			m_stream = m_client.GetStream();
			m_receiveThread = new Thread(ReceiveLoop)
			{
				IsBackground = true,
				Name = "BPRE.Net.Receive"
			};
			m_sendThread = new Thread(SendLoop)
			{
				IsBackground = true,
				Name = "BPRE.Net.Send"
			};
			m_receiveThread.Start();
			m_sendThread.Start();
		}

		public void Send(byte[] message)
		{
			if (IsClosed || message == null)
			{
				return;
			}
			try
			{
				m_outgoing.Add(message);
			}
			catch (InvalidOperationException)
			{
				// Outgoing queue already completed because the connection closed.
			}
		}

		public bool TryReceive(out byte[] message)
		{
			return m_incoming.TryDequeue(out message);
		}

		public void Close(string reason)
		{
			if (Interlocked.Exchange(ref m_closed, 1) != 0)
			{
				return;
			}
			m_closeReason = reason ?? "closed";
			try
			{
				m_outgoing.CompleteAdding();
			}
			catch
			{
			}
			try
			{
				m_stream.Close();
			}
			catch
			{
			}
			try
			{
				m_client.Close();
			}
			catch
			{
			}
		}

		private void ReceiveLoop()
		{
			byte[] header = new byte[4];
			try
			{
				while (!IsClosed)
				{
					ReadExactly(header, 4);
					int length = BitConverter.ToInt32(header, 0);
					if (length <= 0 || length > NetProtocol.MaxMessageSize)
					{
						throw new IOException("Invalid message length " + length);
					}
					byte[] payload = new byte[length];
					ReadExactly(payload, length);
					m_incoming.Enqueue(payload);
				}
			}
			catch (Exception ex)
			{
				Close(IsClosed ? m_closeReason : DescribeError(ex));
			}
		}

		private void ReadExactly(byte[] buffer, int count)
		{
			int offset = 0;
			while (offset < count)
			{
				int read = m_stream.Read(buffer, offset, count - offset);
				if (read <= 0)
				{
					throw new IOException("Connection closed by remote host");
				}
				offset += read;
			}
		}

		private void SendLoop()
		{
			try
			{
				foreach (byte[] message in m_outgoing.GetConsumingEnumerable())
				{
					byte[] header = BitConverter.GetBytes(message.Length);
					m_stream.Write(header, 0, 4);
					m_stream.Write(message, 0, message.Length);
					m_stream.Flush();
				}
			}
			catch (Exception ex)
			{
				Close(IsClosed ? m_closeReason : DescribeError(ex));
			}
		}

		private static string DescribeError(Exception ex)
		{
			if (ex is SocketException se)
			{
				return se.SocketErrorCode.ToString();
			}
			if (ex is IOException && ex.InnerException is SocketException inner)
			{
				return inner.SocketErrorCode.ToString();
			}
			if (ex is ObjectDisposedException)
			{
				return "closed";
			}
			return ex.Message;
		}
	}
}
