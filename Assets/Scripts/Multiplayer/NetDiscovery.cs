using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

namespace BPRE.Multiplayer
{
	/// <summary>Information a host publishes about its session.</summary>
	public struct HostAnnouncement
	{
		public string InstanceId;

		public string HostName;

		public int TcpPort;

		public int Players;

		public int MaxPlayers;

		public MultiplayerModeId Mode;

		public string Level;

		public int GameBuild;
	}

	/// <summary>A session found on the network, as shown in the server list.</summary>
	public sealed class DiscoveredHost
	{
		public HostAnnouncement Info;

		/// <summary>Address the client should connect to (host:port).</summary>
		public string Address;

		public bool IsLoopback;

		public float LastSeen;

		public int PingMs;
	}

	/// <summary>
	/// LAN discovery over UDP. Clients broadcast a small request; every host that receives it
	/// answers directly to the sender with a description of its session.
	/// Broadcasts do not cross routers or VPNs such as Tailscale, so the browser additionally
	/// sends a direct request to the address that was joined last.
	/// </summary>
	public static class NetDiscovery
	{
		public const int DiscoveryPort = 7778;

		private const string RequestMagic = "BPRE-DISCOVER-1";

		private const string ResponseMagic = "BPRE-HERE-1";

		internal static byte[] BuildRequest(long token)
		{
			using (NetWriter writer = new NetWriter(NetMessageType.Ping))
			{
				writer.Write(RequestMagic).Write(NetProtocol.ProtocolVersion).Write(unchecked((int)(token & 0x7fffffff)));
				return writer.ToArray();
			}
		}

		internal static bool TryParseRequest(byte[] data, out int token)
		{
			token = 0;
			try
			{
				using (NetReader reader = new NetReader(data))
				{
					if (reader.Type != NetMessageType.Ping || reader.ReadString() != RequestMagic)
					{
						return false;
					}
					if (reader.ReadInt() != NetProtocol.ProtocolVersion)
					{
						return false;
					}
					token = reader.ReadInt();
					return true;
				}
			}
			catch
			{
				return false;
			}
		}

		internal static byte[] BuildResponse(HostAnnouncement info, int token)
		{
			using (NetWriter writer = new NetWriter(NetMessageType.Pong))
			{
				writer.Write(ResponseMagic)
					.Write(NetProtocol.ProtocolVersion)
					.Write(token)
					.Write(info.InstanceId ?? string.Empty)
					.Write(info.HostName ?? string.Empty)
					.Write(info.TcpPort)
					.Write(info.Players)
					.Write(info.MaxPlayers)
					.Write((byte)info.Mode)
					.Write(info.Level ?? string.Empty)
					.Write(info.GameBuild);
				return writer.ToArray();
			}
		}

		internal static bool TryParseResponse(byte[] data, out HostAnnouncement info, out int token)
		{
			info = default;
			token = 0;
			try
			{
				using (NetReader reader = new NetReader(data))
				{
					if (reader.Type != NetMessageType.Pong || reader.ReadString() != ResponseMagic)
					{
						return false;
					}
					if (reader.ReadInt() != NetProtocol.ProtocolVersion)
					{
						return false;
					}
					token = reader.ReadInt();
					info.InstanceId = reader.ReadString();
					info.HostName = NetProtocol.SanitizeName(reader.ReadString());
					info.TcpPort = reader.ReadInt();
					info.Players = reader.ReadInt();
					info.MaxPlayers = reader.ReadInt();
					info.Mode = (MultiplayerModeId)reader.ReadByte();
					info.Level = reader.ReadString();
					info.GameBuild = reader.ReadInt();
					return info.TcpPort > 0 && info.TcpPort <= 65535 && !string.IsNullOrEmpty(info.InstanceId);
				}
			}
			catch
			{
				return false;
			}
		}

		/// <summary>Broadcast addresses of all active IPv4 interfaces plus the limited broadcast and loopback.</summary>
		internal static List<IPAddress> GetProbeTargets()
		{
			List<IPAddress> targets = new List<IPAddress> { IPAddress.Broadcast, IPAddress.Loopback };
			try
			{
				foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
				{
					if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
					{
						continue;
					}
					foreach (UnicastIPAddressInformation unicast in nic.GetIPProperties().UnicastAddresses)
					{
						if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask == null)
						{
							continue;
						}
						byte[] ip = unicast.Address.GetAddressBytes();
						byte[] mask = unicast.IPv4Mask.GetAddressBytes();
						if (ip[0] == 169 && ip[1] == 254)
						{
							continue; // link-local, no DHCP
						}
						byte[] broadcast = new byte[4];
						for (int i = 0; i < 4; i++)
						{
							broadcast[i] = (byte)(ip[i] | ~mask[i]);
						}
						IPAddress address = new IPAddress(broadcast);
						if (!targets.Contains(address))
						{
							targets.Add(address);
						}
					}
				}
			}
			catch
			{
			}
			return targets;
		}
	}

	/// <summary>Host side: answers discovery requests while a session is hosted.</summary>
	public sealed class DiscoveryResponder
	{
		private UdpClient m_udp;

		private Thread m_thread;

		private volatile bool m_running;

		private HostAnnouncement m_info;

		private readonly object m_lock = new object();

		public bool IsRunning => m_running;

		public string Error { get; private set; }

		public void Start(HostAnnouncement info)
		{
			Stop();
			Update(info);
			try
			{
				UdpClient udp = new UdpClient();
				// Several hosts on one machine may listen at the same time.
				udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
				udp.ExclusiveAddressUse = false;
				udp.Client.Bind(new IPEndPoint(IPAddress.Any, NetDiscovery.DiscoveryPort));
				udp.EnableBroadcast = true;
				m_udp = udp;
			}
			catch (Exception ex)
			{
				Error = ex.Message;
				Debug.LogWarning("[Multiplayer] LAN discovery unavailable: " + ex.Message);
				return;
			}
			m_running = true;
			m_thread = new Thread(Loop) { IsBackground = true, Name = "BPRE.Net.DiscoveryResponder" };
			m_thread.Start();
		}

		public void Update(HostAnnouncement info)
		{
			lock (m_lock)
			{
				m_info = info;
			}
		}

		public void Stop()
		{
			m_running = false;
			UdpClient udp = m_udp;
			m_udp = null;
			try
			{
				udp?.Close();
			}
			catch
			{
			}
		}

		private void Loop()
		{
			UdpClient udp = m_udp;
			while (m_running && udp != null)
			{
				try
				{
					IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
					byte[] data = udp.Receive(ref remote);
					if (!NetDiscovery.TryParseRequest(data, out int token))
					{
						continue;
					}
					HostAnnouncement info;
					lock (m_lock)
					{
						info = m_info;
					}
					byte[] response = NetDiscovery.BuildResponse(info, token);
					udp.Send(response, response.Length, remote);
				}
				catch (SocketException)
				{
					if (!m_running)
					{
						return;
					}
				}
				catch (ObjectDisposedException)
				{
					return;
				}
				catch (Exception)
				{
				}
			}
		}
	}

	/// <summary>Client side: periodically searches for hosts and keeps a list of the ones that answered.</summary>
	public sealed class DiscoveryBrowser
	{
		private const float ProbeInterval = 1.5f;

		private const float HostTimeout = 5f;

		private UdpClient m_udp;

		private Thread m_thread;

		private volatile bool m_running;

		private float m_nextProbe;

		private readonly ConcurrentQueue<KeyValuePair<IPEndPoint, byte[]>> m_incoming = new ConcurrentQueue<KeyValuePair<IPEndPoint, byte[]>>();

		private readonly Dictionary<string, DiscoveredHost> m_hosts = new Dictionary<string, DiscoveredHost>();

		private readonly Dictionary<int, float> m_sentAt = new Dictionary<int, float>();

		private int m_token;

		/// <summary>Extra host names or IPs to ask directly (e.g. the last joined address, for VPNs without broadcast).</summary>
		public List<string> DirectTargets { get; } = new List<string>();

		public bool IsRunning => m_running;

		public string Error { get; private set; }

		public List<DiscoveredHost> Hosts
		{
			get
			{
				List<DiscoveredHost> list = new List<DiscoveredHost>(m_hosts.Values);
				list.Sort((a, b) => string.Compare(a.Info.HostName, b.Info.HostName, StringComparison.OrdinalIgnoreCase));
				return list;
			}
		}

		public void Start()
		{
			if (m_running)
			{
				return;
			}
			try
			{
				UdpClient udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
				udp.EnableBroadcast = true;
				m_udp = udp;
			}
			catch (Exception ex)
			{
				Error = ex.Message;
				return;
			}
			Error = null;
			m_running = true;
			m_nextProbe = 0f;
			m_thread = new Thread(Loop) { IsBackground = true, Name = "BPRE.Net.DiscoveryBrowser" };
			m_thread.Start();
		}

		public void Stop()
		{
			m_running = false;
			UdpClient udp = m_udp;
			m_udp = null;
			try
			{
				udp?.Close();
			}
			catch
			{
			}
			m_hosts.Clear();
			m_sentAt.Clear();
		}

		/// <summary>Call every frame from the main thread while the browser is running.</summary>
		public void Update()
		{
			if (!m_running)
			{
				return;
			}
			float now = Time.realtimeSinceStartup;
			if (now >= m_nextProbe)
			{
				m_nextProbe = now + ProbeInterval;
				SendProbes(now);
			}
			while (m_incoming.TryDequeue(out KeyValuePair<IPEndPoint, byte[]> packet))
			{
				if (!NetDiscovery.TryParseResponse(packet.Value, out HostAnnouncement info, out int token))
				{
					continue;
				}
				IPAddress source = packet.Key.Address;
				bool loopback = IPAddress.IsLoopback(source);
				if (!m_hosts.TryGetValue(info.InstanceId, out DiscoveredHost host))
				{
					host = new DiscoveredHost();
					m_hosts[info.InstanceId] = host;
				}
				// Prefer a real network address over loopback: it works from other machines as well.
				if (host.Address == null || (host.IsLoopback && !loopback))
				{
					host.Address = source + ":" + info.TcpPort;
					host.IsLoopback = loopback;
				}
				host.Info = info;
				host.LastSeen = now;
				if (m_sentAt.TryGetValue(token, out float sent))
				{
					host.PingMs = Mathf.Max(0, Mathf.RoundToInt((now - sent) * 1000f));
				}
			}
			List<string> expired = null;
			foreach (KeyValuePair<string, DiscoveredHost> pair in m_hosts)
			{
				if (now - pair.Value.LastSeen > HostTimeout)
				{
					expired ??= new List<string>();
					expired.Add(pair.Key);
				}
			}
			if (expired != null)
			{
				foreach (string key in expired)
				{
					m_hosts.Remove(key);
				}
			}
		}

		private void SendProbes(float now)
		{
			UdpClient udp = m_udp;
			if (udp == null)
			{
				return;
			}
			int token = ++m_token;
			m_sentAt[token] = now;
			if (m_sentAt.Count > 16)
			{
				m_sentAt.Remove(token - 16);
			}
			byte[] request = NetDiscovery.BuildRequest(token);
			foreach (IPAddress target in NetDiscovery.GetProbeTargets())
			{
				TrySend(udp, request, target);
			}
			foreach (string direct in DirectTargets)
			{
				if (string.IsNullOrEmpty(direct))
				{
					continue;
				}
				if (IPAddress.TryParse(direct, out IPAddress ip))
				{
					TrySend(udp, request, ip);
				}
			}
		}

		private static void TrySend(UdpClient udp, byte[] data, IPAddress target)
		{
			try
			{
				udp.Send(data, data.Length, new IPEndPoint(target, NetDiscovery.DiscoveryPort));
			}
			catch
			{
				// Unreachable interface or missing route: ignore, the other targets still work.
			}
		}

		private void Loop()
		{
			UdpClient udp = m_udp;
			while (m_running && udp != null)
			{
				try
				{
					IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
					byte[] data = udp.Receive(ref remote);
					if (data != null && data.Length > 0 && data.Length < 4096)
					{
						m_incoming.Enqueue(new KeyValuePair<IPEndPoint, byte[]>(remote, data));
					}
				}
				catch (ObjectDisposedException)
				{
					return;
				}
				catch (SocketException ex)
				{
					if (!m_running)
					{
						return;
					}
					// Windows reports ICMP "port unreachable" from earlier sends as ConnectionReset; keep listening.
					if (ex.SocketErrorCode != SocketError.ConnectionReset)
					{
						Thread.Sleep(50);
					}
				}
				catch (Exception)
				{
				}
			}
		}
	}
}
