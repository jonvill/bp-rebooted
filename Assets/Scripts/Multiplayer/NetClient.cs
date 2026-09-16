using System;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

namespace BPRE.Multiplayer
{
	/// <summary>
	/// Non-blocking outgoing TCP connection. Poll Update() from the main thread until
	/// Connection is available or Failed is set.
	/// </summary>
	public sealed class NetClient
	{
		private const int StateConnecting = 0;

		private const int StateConnected = 1;

		private const int StateFailed = 2;

		private TcpClient m_tcp;

		private int m_state;

		private string m_error;

		private float m_startTime;

		private float m_timeout;

		public NetConnection Connection { get; private set; }

		public bool IsConnecting => m_state == StateConnecting && Connection == null;

		public bool Failed => m_state == StateFailed;

		public string Error => m_error;

		public void Connect(string host, int port, float timeoutSeconds)
		{
			Close();
			m_state = StateConnecting;
			m_error = null;
			m_startTime = Time.realtimeSinceStartup;
			m_timeout = timeoutSeconds;
			try
			{
				m_tcp = new TcpClient();
				m_tcp.BeginConnect(host, port, OnConnected, m_tcp);
			}
			catch (Exception ex)
			{
				Fail(ex.Message);
			}
		}

		private void OnConnected(IAsyncResult result)
		{
			TcpClient tcp = (TcpClient)result.AsyncState;
			try
			{
				tcp.EndConnect(result);
				if (tcp != m_tcp)
				{
					tcp.Close();
					return;
				}
				Interlocked.CompareExchange(ref m_state, StateConnected, StateConnecting);
			}
			catch (Exception ex)
			{
				if (tcp == m_tcp)
				{
					Fail(ex is SocketException se ? se.SocketErrorCode.ToString() : ex.Message);
				}
			}
		}

		public void Update()
		{
			if (m_state == StateConnected && Connection == null && m_tcp != null)
			{
				try
				{
					Connection = new NetConnection(m_tcp);
				}
				catch (Exception ex)
				{
					Fail(ex.Message);
				}
			}
			else if (m_state == StateConnecting && Time.realtimeSinceStartup - m_startTime > m_timeout)
			{
				Fail("Timed out");
				TcpClient tcp = m_tcp;
				m_tcp = null;
				try
				{
					tcp?.Close();
				}
				catch
				{
				}
			}
		}

		private void Fail(string reason)
		{
			if (Interlocked.Exchange(ref m_state, StateFailed) != StateFailed)
			{
				m_error = reason;
			}
		}

		public void Close()
		{
			Connection?.Close("client closed");
			Connection = null;
			TcpClient tcp = m_tcp;
			m_tcp = null;
			try
			{
				tcp?.Close();
			}
			catch
			{
			}
		}
	}
}
