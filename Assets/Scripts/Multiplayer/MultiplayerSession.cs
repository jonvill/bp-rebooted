using System;
using System.Collections.Generic;
using UnityEngine;

namespace BPRE.Multiplayer
{
	/// <summary>
	/// Owns the multiplayer session: hosting, joining, the player list, chat,
	/// level following, the selected game mode and relaying of replication messages.
	/// Created automatically at startup, lives across scene loads.
	/// </summary>
	public sealed class MultiplayerSession : MonoBehaviour
	{
		public enum SessionState
		{
			Offline,
			Hosting,
			Connecting,
			Connected
		}

		public struct ChatLine
		{
			public string Sender;

			public string Text;

			public float Time;

			public bool IsSystem;
		}

		private struct HostLocation
		{
			public bool HasLevel;

			public int EpisodeType;

			public string EpisodeScene;

			public int LevelIndex;

			public string Identifier;

			public string SceneName;
		}

		private const string PrefNameKey = "bpre_mp_name";

		private const string PrefAddressKey = "bpre_mp_address";

		private const string PrefPortKey = "bpre_mp_port";

		private const string PrefModeKey = "bpre_mp_mode";

		private const string PrefServerNameKey = "bpre_mp_servername";

		private const int HostPlayerId = 1;

		private const int MaxChatLines = 100;

		private const float HandshakeTimeout = 10f;

		private const int MaxFollowAttempts = 3;

		public static MultiplayerSession Instance { get; private set; }

		/// <summary>
		/// True while this player is in a level together with others. The game then must not
		/// stop time (pause menu, lost window focus, mod interface), because every player
		/// simulates their own contraption and the others keep playing.
		/// </summary>
		public static bool KeepsWorldRunning
		{
			get
			{
				MultiplayerSession session = Instance;
				return session != null && session.IsActive && session.LocalPlayer != null && session.LocalPlayer.InLevel;
			}
		}

		private NetServer m_server;

		private NetClient m_client;

		private NetConnection m_hostConnection;

		private readonly List<MultiplayerPlayer> m_players = new List<MultiplayerPlayer>();

		private readonly List<NetConnection> m_unidentified = new List<NetConnection>();

		private int m_nextPlayerId = HostPlayerId + 1;

		private HostLocation m_hostLocation;

		private HostLocation m_pendingFollow;

		private bool m_hasPendingFollow;

		private int m_followAttempts;

		private float m_followRetryTime;

		private string m_playerName;

		private byte[] m_pingMessage;

		private byte[] m_pongMessage;

		private readonly DiscoveryResponder m_responder = new DiscoveryResponder();

		private readonly DiscoveryBrowser m_browser = new DiscoveryBrowser();

		private string m_instanceId = string.Empty;

		private string m_password = string.Empty;

		private string m_joinPassword = string.Empty;

		private readonly HashSet<string> m_bannedIps = new HashSet<string>();

		private readonly HashSet<string> m_bannedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		private sealed class PendingAuth
		{
			public string Name;

			public string Nonce;
		}

		private readonly Dictionary<NetConnection, PendingAuth> m_pendingAuth = new Dictionary<NetConnection, PendingAuth>();

		private float m_nextAnnouncementUpdate;

		public SessionState State { get; private set; }

		public bool IsActive => State == SessionState.Hosting || State == SessionState.Connected;

		public bool IsHost => State == SessionState.Hosting;

		public MultiplayerPlayer LocalPlayer { get; private set; }

		public IReadOnlyList<MultiplayerPlayer> Players => m_players;

		public string LastError { get; private set; }

		public int HostPort { get; private set; }

		public string ConnectedAddress { get; private set; }

		public List<ChatLine> Chat { get; } = new List<ChatLine>();

		/// <summary>Incremented whenever a chat line is added, so the UI can auto-scroll.</summary>
		public int ChatVersion { get; private set; }

		/// <summary>The game mode currently selected by the host. Null while offline.</summary>
		public MultiplayerGameMode CurrentMode { get; private set; }

		public MultiplayerModeId CurrentModeId => CurrentMode != null ? CurrentMode.Id : PreferredModeId;

		public event Action<MultiplayerPlayer> PlayerJoined;

		public event Action<MultiplayerPlayer> PlayerLeft;

		public event Action<MultiplayerPlayer> PlayerLocationChanged;

		public event Action LocalLocationChanged;

		public event Action SessionStarted;

		public event Action SessionEnded;

		public event Action<MultiplayerGameMode> ModeChanged;

		/// <summary>
		/// Raised for ContraptionStart/State/Stop messages of other players.
		/// The reader is positioned right after the type byte; the first int is the owner id.
		/// </summary>
		public event Action<MultiplayerPlayer, NetReader> ContraptionMessage;

		public string PlayerName
		{
			get
			{
				if (string.IsNullOrEmpty(m_playerName))
				{
					m_playerName = NetProtocol.SanitizeName(PlayerPrefs.GetString(PrefNameKey, string.Empty));
					if (string.IsNullOrEmpty(m_playerName))
					{
						m_playerName = "Piggy" + UnityEngine.Random.Range(100, 999);
					}
				}
				return m_playerName;
			}
			set
			{
				string sanitized = NetProtocol.SanitizeName(value);
				if (string.IsNullOrEmpty(sanitized) || sanitized == m_playerName)
				{
					return;
				}
				m_playerName = sanitized;
				PlayerPrefs.SetString(PrefNameKey, sanitized);
				PlayerPrefs.Save();
			}
		}

		public string LastJoinAddress
		{
			get => PlayerPrefs.GetString(PrefAddressKey, "127.0.0.1:" + NetProtocol.DefaultPort);
			private set
			{
				PlayerPrefs.SetString(PrefAddressKey, value ?? string.Empty);
				PlayerPrefs.Save();
			}
		}

		public int LastHostPort
		{
			get => PlayerPrefs.GetInt(PrefPortKey, NetProtocol.DefaultPort);
			private set
			{
				PlayerPrefs.SetInt(PrefPortKey, value);
				PlayerPrefs.Save();
			}
		}

		/// <summary>Mode a new hosted session starts with.</summary>
		public MultiplayerModeId PreferredModeId
		{
			get => (MultiplayerModeId)Mathf.Clamp(PlayerPrefs.GetInt(PrefModeKey, 0), 0, (int)MultiplayerModeId.Battle);
			set
			{
				PlayerPrefs.SetInt(PrefModeKey, (int)value);
				PlayerPrefs.Save();
			}
		}

		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
		private static void Bootstrap()
		{
			if (Instance != null)
			{
				return;
			}
			GameObject go = new GameObject("BPRE_Multiplayer");
			UnityEngine.Object.DontDestroyOnLoad(go);
			go.AddComponent<MultiplayerSession>();
			go.AddComponent<ContraptionSync>();
			go.AddComponent<MultiplayerUI>();
		}

		private void Awake()
		{
			if (Instance != null && Instance != this)
			{
				UnityEngine.Object.Destroy(this);
				return;
			}
			Instance = this;
			m_pingMessage = new NetWriter(NetMessageType.Ping).ToArray();
			m_pongMessage = new NetWriter(NetMessageType.Pong).ToArray();
			EventManager.Connect<LoadLevelEvent>(OnLoadLevel);
			EventManager.Connect<GameLevelLoaded>(OnLevelLoaded);
		}

		private void OnDestroy()
		{
			if (Instance != this)
			{
				return;
			}
			EventManager.Disconnect<LoadLevelEvent>(OnLoadLevel);
			EventManager.Disconnect<GameLevelLoaded>(OnLevelLoaded);
			Leave();
			m_browser.Stop();
			Instance = null;
		}

		private void OnApplicationQuit()
		{
			Leave();
			m_browser.Stop();
		}

		// ------------------------------------------------------------------
		// Public API
		// ------------------------------------------------------------------

		/// <summary>Name shown in the server list and to joined players.</summary>
		public string ServerName { get; private set; } = string.Empty;

		/// <summary>Set when the last join attempt failed because a (correct) password is required; the UI then asks for it.</summary>
		public string NeedsPasswordFor { get; set; }

		/// <summary>Host: true when joining requires the password.</summary>
		public bool HasPassword => !string.IsNullOrEmpty(m_password);

		/// <summary>Last server name the host used (remembered between sessions).</summary>
		public string PreferredServerName
		{
			get
			{
				string name = NetProtocol.SanitizeServerName(PlayerPrefs.GetString(PrefServerNameKey, string.Empty));
				return string.IsNullOrEmpty(name) ? NetProtocol.SanitizeServerName(PlayerName + "'s game") : name;
			}
			set
			{
				PlayerPrefs.SetString(PrefServerNameKey, NetProtocol.SanitizeServerName(value));
				PlayerPrefs.Save();
			}
		}

		public bool Host(int port)
		{
			return Host(port, PreferredServerName, null);
		}

		public bool Host(int port, string serverName, string password)
		{
			LastError = null;
			Leave();
			if (port <= 0 || port > 65535)
			{
				LastError = "Invalid port " + port;
				return false;
			}
			serverName = NetProtocol.SanitizeServerName(serverName);
			if (string.IsNullOrEmpty(serverName))
			{
				serverName = NetProtocol.SanitizeServerName(PlayerName + "'s game");
			}
			password = password ?? string.Empty;
			if (password.Length > NetProtocol.MaxPasswordLength)
			{
				password = password.Substring(0, NetProtocol.MaxPasswordLength);
			}
			NetServer server = new NetServer();
			try
			{
				server.Start(port);
			}
			catch (Exception ex)
			{
				LastError = "Could not host on port " + port + ": " + ex.Message;
				server.Stop();
				return false;
			}
			m_server = server;
			HostPort = port;
			LastHostPort = port;
			m_instanceId = Guid.NewGuid().ToString("N");
			ServerName = serverName;
			PreferredServerName = serverName;
			m_password = password;
			m_bannedIps.Clear();
			m_bannedNames.Clear();
			m_pendingAuth.Clear();
			m_nextPlayerId = HostPlayerId + 1;
			LocalPlayer = new MultiplayerPlayer
			{
				Id = HostPlayerId,
				Name = PlayerName,
				IsHost = true,
				IsLocal = true
			};
			m_players.Clear();
			m_players.Add(LocalPlayer);
			State = SessionState.Hosting;
			List<string> addresses = NetServer.GetLocalAddresses();
			string hint = addresses.Count > 0 ? " Your addresses: " + string.Join(", ", addresses) : string.Empty;
			AddSystemChat("Hosting \"" + serverName + "\" on port " + port + (HasPassword ? " (password protected)." : ".") + hint);
			ActivateMode(PreferredModeId, announce: false);
			RefreshLocalLocation();
			SetBrowsing(false);
			m_responder.Start(BuildAnnouncement());
			m_nextAnnouncementUpdate = 0f;
			SessionStarted?.Invoke();
			CurrentMode?.OnSessionStarted();
			return true;
		}

		// ------------------------------------------------------------------
		// LAN discovery
		// ------------------------------------------------------------------

		/// <summary>Sessions found on the local network (only filled while browsing).</summary>
		public List<DiscoveredHost> DiscoveredHosts => m_browser.Hosts;

		public bool IsBrowsing => m_browser.IsRunning;

		public string DiscoveryError => IsHost ? m_responder.Error : m_browser.Error;

		/// <summary>Starts or stops searching for hosts. Only possible while offline.</summary>
		public void SetBrowsing(bool browse)
		{
			browse &= State == SessionState.Offline;
			if (browse == m_browser.IsRunning)
			{
				return;
			}
			if (browse)
			{
				m_browser.DirectTargets.Clear();
				if (TryParseAddress(LastJoinAddress, out string lastHost, out _) && lastHost != "127.0.0.1")
				{
					m_browser.DirectTargets.Add(lastHost);
				}
				m_browser.Start();
			}
			else
			{
				m_browser.Stop();
			}
		}

		private HostAnnouncement BuildAnnouncement()
		{
			string level = LocalPlayer != null && LocalPlayer.InLevel ? LocalPlayer.LocationLabel : "menu";
			return new HostAnnouncement
			{
				InstanceId = m_instanceId,
				HostName = ServerName,
				HasPassword = HasPassword,
				TcpPort = HostPort,
				Players = m_players.Count,
				MaxPlayers = NetProtocol.MaxPlayers,
				Mode = CurrentModeId,
				Level = level,
				GameBuild = BPRE.Updater.BuildInfo.Current.build
			};
		}

		public bool Join(string address)
		{
			return Join(address, null);
		}

		public bool Join(string address, string password)
		{
			LastError = null;
			Leave();
			m_joinPassword = password ?? string.Empty;
			ServerName = string.Empty;
			if (!TryParseAddress(address, out string host, out int port))
			{
				LastError = "Invalid address. Use host or host:port";
				return false;
			}
			m_client = new NetClient();
			LocalPlayer = new MultiplayerPlayer
			{
				Id = 0,
				Name = PlayerName,
				IsLocal = true
			};
			m_players.Clear();
			ConnectedAddress = host + ":" + port;
			State = SessionState.Connecting;
			m_client.Connect(host, port, NetProtocol.ConnectTimeout);
			AddSystemChat("Connecting to " + ConnectedAddress + " ...");
			return true;
		}

		public void Leave()
		{
			bool wasActive = State != SessionState.Offline;
			if (CurrentMode != null)
			{
				MultiplayerGameMode mode = CurrentMode;
				CurrentMode = null;
				try
				{
					mode.OnExit();
				}
				catch (Exception ex)
				{
					Debug.LogWarning("[Multiplayer] Mode cleanup failed: " + ex.Message);
				}
			}
			m_responder.Stop();
			if (m_server != null)
			{
				m_server.Stop();
				m_server = null;
			}
			if (m_client != null)
			{
				m_client.Close();
				m_client = null;
			}
			m_hostConnection = null;
			m_unidentified.Clear();
			m_pendingAuth.Clear();
			m_password = string.Empty;
			foreach (MultiplayerPlayer player in m_players)
			{
				player.Connection = null;
			}
			m_players.Clear();
			LocalPlayer = null;
			m_hasPendingFollow = false;
			m_hostLocation = default;
			State = SessionState.Offline;
			if (wasActive)
			{
				AddSystemChat(string.IsNullOrEmpty(LastError) ? "Left the session." : "Session ended: " + LastError);
				SessionEnded?.Invoke();
			}
		}

		public MultiplayerPlayer GetPlayer(int id)
		{
			for (int i = 0; i < m_players.Count; i++)
			{
				if (m_players[i].Id == id)
				{
					return m_players[i];
				}
			}
			return null;
		}

		/// <summary>Sends a message to every other participant (the host relays for clients).</summary>
		public void SendToAll(byte[] message)
		{
			if (message == null)
			{
				return;
			}
			if (State == SessionState.Hosting)
			{
				m_server.Broadcast(message);
			}
			else if (State == SessionState.Connected && m_hostConnection != null)
			{
				m_hostConnection.Send(message);
			}
		}

		/// <summary>Host only: sends a message to one player.</summary>
		public void SendTo(MultiplayerPlayer player, byte[] message)
		{
			if (State != SessionState.Hosting || player == null || player.IsLocal || player.Connection == null)
			{
				return;
			}
			player.Connection.Send(message);
		}

		public void SendChat(string text)
		{
			text = NetProtocol.SanitizeChat(text);
			if (string.IsNullOrEmpty(text) || !IsActive || LocalPlayer == null)
			{
				return;
			}
			byte[] message = BuildChat(LocalPlayer.Id, text);
			if (IsHost)
			{
				m_server.Broadcast(message);
				AddChat(LocalPlayer.Name, text);
			}
			else
			{
				// The host echoes the line back to everyone, including us.
				m_hostConnection.Send(message);
			}
		}

		/// <summary>Host: switches the session to another game mode and tells everybody.</summary>
		public bool SelectMode(MultiplayerModeId id)
		{
			if (State == SessionState.Offline)
			{
				PreferredModeId = id;
				return true;
			}
			if (!IsHost)
			{
				return false;
			}
			PreferredModeId = id;
			ActivateMode(id, announce: true);
			m_server.Broadcast(BuildModeSelect(id));
			return true;
		}

		/// <summary>Sends a mode specific message to all other players (relayed by the host).</summary>
		public void SendModeMessage(byte subType, Action<NetWriter> payload)
		{
			if (!IsActive || LocalPlayer == null || CurrentMode == null)
			{
				return;
			}
			using (NetWriter writer = new NetWriter(NetMessageType.ModeMessage))
			{
				writer.Write(LocalPlayer.Id).Write((byte)CurrentMode.Id).Write(subType);
				payload?.Invoke(writer);
				SendToAll(writer.ToArray());
			}
		}

		/// <summary>Host only: sends a mode specific message to one player.</summary>
		public void SendModeMessageTo(MultiplayerPlayer player, byte subType, Action<NetWriter> payload)
		{
			if (!IsHost || LocalPlayer == null || CurrentMode == null)
			{
				return;
			}
			using (NetWriter writer = new NetWriter(NetMessageType.ModeMessage))
			{
				writer.Write(LocalPlayer.Id).Write((byte)CurrentMode.Id).Write(subType);
				payload?.Invoke(writer);
				SendTo(player, writer.ToArray());
			}
		}

		public string StatusText
		{
			get
			{
				switch (State)
				{
				case SessionState.Hosting:
					return ServerName + "  -  " + m_players.Count + " player" + (m_players.Count == 1 ? "" : "s") + (HasPassword ? "  -  password" : string.Empty);
				case SessionState.Connecting:
					return "Connecting to " + ConnectedAddress + " ...";
				case SessionState.Connected:
					return ServerName + "  -  " + m_players.Count + " player" + (m_players.Count == 1 ? "" : "s");
				default:
					return "Offline";
				}
			}
		}

		// ------------------------------------------------------------------
		// Update loop
		// ------------------------------------------------------------------

		private void Update()
		{
			MultiplayerCommands.TryRegister();
			if (m_browser.IsRunning)
			{
				if (State == SessionState.Offline)
				{
					m_browser.Update();
				}
				else
				{
					m_browser.Stop();
				}
			}
			if (State == SessionState.Hosting && Time.realtimeSinceStartup >= m_nextAnnouncementUpdate)
			{
				m_nextAnnouncementUpdate = Time.realtimeSinceStartup + 0.5f;
				m_responder.Update(BuildAnnouncement());
			}
			switch (State)
			{
			case SessionState.Hosting:
				UpdateServer();
				break;
			case SessionState.Connecting:
				UpdateConnecting();
				break;
			case SessionState.Connected:
				UpdateClient();
				break;
			}
			if (m_hasPendingFollow && State == SessionState.Connected && Time.realtimeSinceStartup >= m_followRetryTime)
			{
				TryFollow();
			}
			if (IsActive && CurrentMode != null)
			{
				try
				{
					CurrentMode.Update();
				}
				catch (Exception ex)
				{
					Debug.LogWarning("[Multiplayer] Mode update failed: " + ex);
				}
			}
		}

		private void UpdateServer()
		{
			float now = Time.realtimeSinceStartup;
			if (!m_server.IsRunning)
			{
				LastError = "Server stopped";
				Leave();
				return;
			}
			foreach (NetConnection connection in m_server.Update())
			{
				connection.LastActivityTime = now;
				connection.LastPingSentTime = now;
				m_unidentified.Add(connection);
			}
			for (int i = m_unidentified.Count - 1; i >= 0; i--)
			{
				NetConnection connection = m_unidentified[i];
				DrainConnection(connection, now, isServer: true);
				if (State != SessionState.Hosting)
				{
					return;
				}
				if (connection.PlayerId != 0)
				{
					// Handshake completed, the connection now belongs to a player.
					m_unidentified.RemoveAt(i);
					continue;
				}
				if (connection.IsClosed)
				{
					m_unidentified.RemoveAt(i);
					m_pendingAuth.Remove(connection);
					m_server.Remove(connection, connection.CloseReason);
				}
				else if (now - connection.LastActivityTime > HandshakeTimeout)
				{
					m_unidentified.RemoveAt(i);
					m_pendingAuth.Remove(connection);
					m_server.Remove(connection, "handshake timeout");
				}
			}
			MultiplayerPlayer[] players = m_players.ToArray();
			foreach (MultiplayerPlayer player in players)
			{
				NetConnection connection = player.Connection;
				if (player.IsLocal || connection == null)
				{
					continue;
				}
				DrainConnection(connection, now, isServer: true);
				if (State != SessionState.Hosting)
				{
					return;
				}
				if (connection.IsClosed)
				{
					RemovePlayer(player, connection.CloseReason);
				}
				else if (now - connection.LastActivityTime > NetProtocol.ConnectionTimeout)
				{
					RemovePlayer(player, "timeout");
				}
				else if (now - connection.LastPingSentTime > NetProtocol.PingInterval)
				{
					connection.Send(m_pingMessage);
					connection.LastPingSentTime = now;
				}
			}
		}

		private void UpdateConnecting()
		{
			float now = Time.realtimeSinceStartup;
			m_client.Update();
			if (m_client.Failed)
			{
				LastError = "Could not connect: " + m_client.Error;
				Leave();
				return;
			}
			if (m_client.Connection != null && m_hostConnection == null)
			{
				m_hostConnection = m_client.Connection;
				m_hostConnection.LastActivityTime = now;
				using (NetWriter writer = new NetWriter(NetMessageType.Hello))
				{
					writer.Write(NetProtocol.ProtocolVersion).Write(PlayerName);
					m_hostConnection.Send(writer.ToArray());
				}
			}
			if (m_hostConnection != null)
			{
				UpdateClient();
			}
		}

		private void UpdateClient()
		{
			float now = Time.realtimeSinceStartup;
			NetConnection connection = m_hostConnection;
			if (connection == null)
			{
				return;
			}
			DrainConnection(connection, now, isServer: false);
			if (State == SessionState.Offline || m_hostConnection != connection)
			{
				return;
			}
			if (connection.IsClosed)
			{
				LastError = "Connection lost (" + connection.CloseReason + ")";
				Leave();
			}
			else if (now - connection.LastActivityTime > NetProtocol.ConnectionTimeout)
			{
				LastError = "Connection timed out";
				Leave();
			}
		}

		private void DrainConnection(NetConnection connection, float now, bool isServer)
		{
			int budget = 256;
			while (budget-- > 0 && connection.TryReceive(out byte[] message))
			{
				connection.LastActivityTime = now;
				try
				{
					using (NetReader reader = new NetReader(message))
					{
						if (isServer)
						{
							HandleServerMessage(connection, reader);
						}
						else
						{
							HandleClientMessage(reader);
						}
					}
				}
				catch (Exception ex)
				{
					Debug.LogWarning("[Multiplayer] Dropped malformed message from " + connection.RemoteAddress + ": " + ex.Message);
				}
				if (State == SessionState.Offline || connection.IsClosed)
				{
					return;
				}
			}
		}

		// ------------------------------------------------------------------
		// Host side
		// ------------------------------------------------------------------

		private void HandleServerMessage(NetConnection connection, NetReader reader)
		{
			MultiplayerPlayer sender = connection.PlayerId != 0 ? GetPlayer(connection.PlayerId) : null;
			switch (reader.Type)
			{
			case NetMessageType.Hello:
				if (sender == null && !m_pendingAuth.ContainsKey(connection))
				{
					HandleHello(connection, reader);
				}
				break;
			case NetMessageType.AuthResponse:
				if (sender == null)
				{
					HandleAuthResponse(connection, reader);
				}
				break;
			case NetMessageType.Pong:
				break;
			case NetMessageType.Chat:
			{
				if (sender == null)
				{
					break;
				}
				int fromId = reader.ReadInt();
				string text = NetProtocol.SanitizeChat(reader.ReadString());
				if (fromId != sender.Id || string.IsNullOrEmpty(text))
				{
					break;
				}
				m_server.Broadcast(BuildChat(sender.Id, text));
				AddChat(sender.Name, text);
				break;
			}
			case NetMessageType.PlayerLocation:
			{
				if (sender == null)
				{
					break;
				}
				int id = reader.ReadInt();
				string scene = reader.ReadString();
				string identifier = reader.ReadString();
				if (id != sender.Id)
				{
					break;
				}
				sender.SceneName = scene;
				sender.LevelIdentifier = identifier;
				m_server.Broadcast(reader.Raw, connection);
				PlayerLocationChanged?.Invoke(sender);
				CurrentMode?.OnPlayerLocationChanged(sender);
				break;
			}
			case NetMessageType.ContraptionStart:
			case NetMessageType.ContraptionState:
			case NetMessageType.ContraptionStop:
			{
				if (sender == null)
				{
					break;
				}
				int id = reader.ReadInt();
				if (id != sender.Id)
				{
					break;
				}
				if (reader.Type == NetMessageType.ContraptionStart)
				{
					sender.LastContraptionStart = reader.Raw;
				}
				else if (reader.Type == NetMessageType.ContraptionStop)
				{
					sender.LastContraptionStart = null;
				}
				m_server.Broadcast(reader.Raw, connection);
				DispatchContraptionMessage(sender, reader.Raw);
				break;
			}
			case NetMessageType.ModeMessage:
			{
				if (sender == null)
				{
					break;
				}
				int id = reader.ReadInt();
				byte modeId = reader.ReadByte();
				byte subType = reader.ReadByte();
				if (id != sender.Id)
				{
					break;
				}
				m_server.Broadcast(reader.Raw, connection);
				DispatchModeMessage(sender, modeId, subType, reader);
				break;
			}
			}
		}

		private void HandleHello(NetConnection connection, NetReader reader)
		{
			int version = reader.ReadInt();
			string name = NetProtocol.SanitizeName(reader.ReadString());
			if (version != NetProtocol.ProtocolVersion)
			{
				Reject(connection, "Different game version (host v" + NetProtocol.ProtocolVersion + ", you v" + version + "). Please update.");
				return;
			}
			if (m_players.Count >= NetProtocol.MaxPlayers)
			{
				Reject(connection, "This game is full.");
				return;
			}
			if (string.IsNullOrEmpty(name))
			{
				name = "Player";
			}
			if (m_bannedIps.Contains(connection.RemoteIp) || m_bannedNames.Contains(name))
			{
				Reject(connection, "You are banned from this game.");
				return;
			}
			if (HasPassword)
			{
				// Ask for proof of the password instead of having it sent in plain text.
				string nonce = Guid.NewGuid().ToString("N");
				m_pendingAuth[connection] = new PendingAuth { Name = name, Nonce = nonce };
				using (NetWriter writer = new NetWriter(NetMessageType.AuthChallenge))
				{
					writer.Write(nonce);
					connection.Send(writer.ToArray());
				}
				return;
			}
			AcceptPlayer(connection, name);
		}

		private void HandleAuthResponse(NetConnection connection, NetReader reader)
		{
			if (!m_pendingAuth.TryGetValue(connection, out PendingAuth pending))
			{
				return;
			}
			m_pendingAuth.Remove(connection);
			string proof = reader.ReadString();
			if (!string.Equals(proof, NetProtocol.PasswordProof(pending.Nonce, m_password), StringComparison.Ordinal))
			{
				AddSystemChat(pending.Name + " tried to join with a wrong password.");
				Reject(connection, "Wrong password.");
				return;
			}
			if (m_players.Count >= NetProtocol.MaxPlayers)
			{
				Reject(connection, "This game is full.");
				return;
			}
			AcceptPlayer(connection, pending.Name);
		}

		/// <summary>Host: removes a player. With <paramref name="ban"/> the player cannot rejoin this session (by address and name).</summary>
		public void KickPlayer(int playerId, bool ban)
		{
			if (!IsHost)
			{
				return;
			}
			MultiplayerPlayer player = GetPlayer(playerId);
			if (player == null || player.IsLocal)
			{
				return;
			}
			if (ban)
			{
				if (player.Connection != null && player.Connection.RemoteIp != "?")
				{
					m_bannedIps.Add(player.Connection.RemoteIp);
				}
				m_bannedNames.Add(player.Name);
			}
			NetConnection connection = player.Connection;
			if (connection != null)
			{
				using (NetWriter writer = new NetWriter(NetMessageType.Reject))
				{
					writer.Write(ban ? "You were banned from this game by the host." : "You were removed from this game by the host.");
					connection.Send(writer.ToArray());
				}
			}
			RemovePlayer(player, ban ? "banned" : "kicked", closeLater: true);
		}

		public int BannedCount => m_bannedNames.Count;

		/// <summary>Host: lifts all bans of this session.</summary>
		public void ClearBans()
		{
			m_bannedIps.Clear();
			m_bannedNames.Clear();
		}

		private void AcceptPlayer(NetConnection connection, string name)
		{
			name = MakeUniqueName(name);
			MultiplayerPlayer player = new MultiplayerPlayer
			{
				Id = m_nextPlayerId++,
				Name = name,
				Connection = connection
			};
			connection.PlayerId = player.Id;
			m_players.Add(player);
			using (NetWriter writer = new NetWriter(NetMessageType.Welcome))
			{
				writer.Write(player.Id).Write(HostPlayerId).Write(ServerName);
				connection.Send(writer.ToArray());
			}
			connection.Send(BuildPlayerList());
			using (NetWriter writer = new NetWriter(NetMessageType.PlayerJoined))
			{
				writer.Write(player.Id).Write(player.Name);
				m_server.Broadcast(writer.ToArray(), connection);
			}
			if (CurrentMode != null)
			{
				connection.Send(BuildModeSelect(CurrentMode.Id));
			}
			if (m_hostLocation.HasLevel)
			{
				connection.Send(BuildHostLocation(m_hostLocation));
			}
			foreach (MultiplayerPlayer other in m_players)
			{
				if (other != player && other.LastContraptionStart != null)
				{
					connection.Send(other.LastContraptionStart);
				}
			}
			AddSystemChat(player.Name + " joined.");
			PlayerJoined?.Invoke(player);
			CurrentMode?.OnPlayerJoined(player);
		}

		private void Reject(NetConnection connection, string reason)
		{
			using (NetWriter writer = new NetWriter(NetMessageType.Reject))
			{
				writer.Write(reason);
				connection.Send(writer.ToArray());
			}
			// Give the send thread a moment to flush before the socket closes.
			StartCoroutine(CloseLater(connection, reason));
		}

		private System.Collections.IEnumerator CloseLater(NetConnection connection, string reason)
		{
			yield return new WaitForSecondsRealtime(0.5f);
			m_unidentified.Remove(connection);
			m_server?.Remove(connection, reason);
		}

		private void RemovePlayer(MultiplayerPlayer player, string reason, bool closeLater = false)
		{
			if (!m_players.Remove(player))
			{
				return;
			}
			if (m_server != null && player.Connection != null)
			{
				if (closeLater)
				{
					// Let the reason message reach the player before the socket closes.
					StartCoroutine(CloseLater(player.Connection, reason));
				}
				else
				{
					m_server.Remove(player.Connection, reason);
				}
			}
			player.Connection = null;
			using (NetWriter writer = new NetWriter(NetMessageType.PlayerLeft))
			{
				writer.Write(player.Id);
				m_server?.Broadcast(writer.ToArray());
			}
			AddSystemChat(player.Name + " left (" + reason + ").");
			PlayerLeft?.Invoke(player);
			CurrentMode?.OnPlayerLeft(player);
		}

		private string MakeUniqueName(string name)
		{
			string candidate = name;
			int suffix = 2;
			while (NameTaken(candidate))
			{
				string tail = " (" + suffix++ + ")";
				int keep = Math.Min(name.Length, NetProtocol.MaxNameLength - tail.Length);
				candidate = name.Substring(0, keep) + tail;
			}
			return candidate;
		}

		private bool NameTaken(string name)
		{
			foreach (MultiplayerPlayer player in m_players)
			{
				if (string.Equals(player.Name, name, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}
			return false;
		}

		// ------------------------------------------------------------------
		// Client side
		// ------------------------------------------------------------------

		private void HandleClientMessage(NetReader reader)
		{
			switch (reader.Type)
			{
			case NetMessageType.AuthChallenge:
			{
				string nonce = reader.ReadString();
				if (State != SessionState.Connecting)
				{
					break;
				}
				if (string.IsNullOrEmpty(m_joinPassword))
				{
					LastError = "This game needs a password.";
					NeedsPasswordFor = ConnectedAddress;
					Leave();
					break;
				}
				using (NetWriter writer = new NetWriter(NetMessageType.AuthResponse))
				{
					writer.Write(NetProtocol.PasswordProof(nonce, m_joinPassword));
					m_hostConnection?.Send(writer.ToArray());
				}
				break;
			}
			case NetMessageType.Welcome:
			{
				int myId = reader.ReadInt();
				reader.ReadInt();
				string serverName = NetProtocol.SanitizeServerName(reader.ReadString());
				if (State != SessionState.Connecting || LocalPlayer == null)
				{
					break;
				}
				LocalPlayer.Id = myId;
				if (GetPlayer(myId) == null)
				{
					m_players.Add(LocalPlayer);
				}
				ServerName = serverName;
				NeedsPasswordFor = null;
				State = SessionState.Connected;
				LastJoinAddress = ConnectedAddress;
				AddSystemChat("Joined \"" + serverName + "\". Waiting for the host to pick a level.");
				RefreshLocalLocation();
				SessionStarted?.Invoke();
				break;
			}
			case NetMessageType.Reject:
				LastError = reader.ReadString();
				if (LastError == "Wrong password.")
				{
					NeedsPasswordFor = ConnectedAddress;
				}
				Leave();
				break;
			case NetMessageType.PlayerList:
			{
				int count = reader.ReadInt();
				List<MultiplayerPlayer> updated = new List<MultiplayerPlayer>(count);
				for (int i = 0; i < count; i++)
				{
					int id = reader.ReadInt();
					string name = reader.ReadString();
					bool isHost = reader.ReadBool();
					string scene = reader.ReadString();
					string identifier = reader.ReadString();
					MultiplayerPlayer player = GetPlayer(id);
					if (player == null)
					{
						player = (LocalPlayer != null && LocalPlayer.Id == id) ? LocalPlayer : new MultiplayerPlayer { Id = id };
					}
					player.Name = name;
					player.IsHost = isHost;
					if (!player.IsLocal)
					{
						player.SceneName = scene;
						player.LevelIdentifier = identifier;
					}
					updated.Add(player);
				}
				m_players.Clear();
				m_players.AddRange(updated);
				break;
			}
			case NetMessageType.PlayerJoined:
			{
				int id = reader.ReadInt();
				string name = reader.ReadString();
				if (GetPlayer(id) != null)
				{
					break;
				}
				MultiplayerPlayer player = new MultiplayerPlayer { Id = id, Name = name };
				m_players.Add(player);
				AddSystemChat(name + " joined.");
				PlayerJoined?.Invoke(player);
				CurrentMode?.OnPlayerJoined(player);
				break;
			}
			case NetMessageType.PlayerLeft:
			{
				int id = reader.ReadInt();
				MultiplayerPlayer player = GetPlayer(id);
				if (player == null)
				{
					break;
				}
				m_players.Remove(player);
				AddSystemChat(player.Name + " left.");
				PlayerLeft?.Invoke(player);
				CurrentMode?.OnPlayerLeft(player);
				break;
			}
			case NetMessageType.Chat:
			{
				int fromId = reader.ReadInt();
				string text = reader.ReadString();
				MultiplayerPlayer player = GetPlayer(fromId);
				AddChat(player != null ? player.Name : "#" + fromId, text);
				break;
			}
			case NetMessageType.HostLocation:
			{
				HostLocation location = new HostLocation
				{
					HasLevel = reader.ReadBool(),
					EpisodeType = reader.ReadInt(),
					EpisodeScene = reader.ReadString(),
					LevelIndex = reader.ReadInt(),
					Identifier = reader.ReadString(),
					SceneName = reader.ReadString()
				};
				FollowHost(location);
				break;
			}
			case NetMessageType.PlayerLocation:
			{
				int id = reader.ReadInt();
				string scene = reader.ReadString();
				string identifier = reader.ReadString();
				MultiplayerPlayer player = GetPlayer(id);
				if (player == null || player.IsLocal)
				{
					break;
				}
				player.SceneName = scene;
				player.LevelIdentifier = identifier;
				PlayerLocationChanged?.Invoke(player);
				CurrentMode?.OnPlayerLocationChanged(player);
				break;
			}
			case NetMessageType.ContraptionStart:
			case NetMessageType.ContraptionState:
			case NetMessageType.ContraptionStop:
			{
				int id = reader.ReadInt();
				MultiplayerPlayer player = GetPlayer(id);
				if (player == null || player.IsLocal)
				{
					break;
				}
				if (reader.Type == NetMessageType.ContraptionStart)
				{
					player.LastContraptionStart = reader.Raw;
				}
				else if (reader.Type == NetMessageType.ContraptionStop)
				{
					player.LastContraptionStart = null;
				}
				DispatchContraptionMessage(player, reader.Raw);
				break;
			}
			case NetMessageType.ModeSelect:
			{
				MultiplayerModeId id = (MultiplayerModeId)reader.ReadByte();
				ActivateMode(id, announce: true);
				break;
			}
			case NetMessageType.ModeMessage:
			{
				int id = reader.ReadInt();
				byte modeId = reader.ReadByte();
				byte subType = reader.ReadByte();
				MultiplayerPlayer player = GetPlayer(id);
				if (player == null || player.IsLocal)
				{
					break;
				}
				DispatchModeMessage(player, modeId, subType, reader);
				break;
			}
			case NetMessageType.Ping:
				m_hostConnection?.Send(m_pongMessage);
				break;
			}
		}

		private void DispatchContraptionMessage(MultiplayerPlayer player, byte[] raw)
		{
			if (ContraptionMessage == null)
			{
				return;
			}
			using (NetReader reader = new NetReader(raw))
			{
				ContraptionMessage(player, reader);
			}
		}

		private void DispatchModeMessage(MultiplayerPlayer player, byte modeId, byte subType, NetReader reader)
		{
			if (CurrentMode == null || (byte)CurrentMode.Id != modeId)
			{
				return;
			}
			try
			{
				CurrentMode.OnModeMessage(player, subType, reader);
			}
			catch (Exception ex)
			{
				Debug.LogWarning("[Multiplayer] Mode message " + subType + " from " + player.Name + " failed: " + ex.Message);
			}
		}

		// ------------------------------------------------------------------
		// Game modes
		// ------------------------------------------------------------------

		private void ActivateMode(MultiplayerModeId id, bool announce)
		{
			if (CurrentMode != null && CurrentMode.Id == id)
			{
				return;
			}
			if (CurrentMode != null)
			{
				MultiplayerGameMode old = CurrentMode;
				CurrentMode = null;
				try
				{
					old.OnExit();
				}
				catch (Exception ex)
				{
					Debug.LogWarning("[Multiplayer] Mode cleanup failed: " + ex.Message);
				}
			}
			MultiplayerGameMode mode = MultiplayerGameMode.Create(id, this);
			CurrentMode = mode;
			try
			{
				mode.OnEnter();
			}
			catch (Exception ex)
			{
				Debug.LogWarning("[Multiplayer] Mode setup failed: " + ex);
			}
			if (announce)
			{
				AddSystemChat("Game mode: " + mode.DisplayName + " - " + mode.Description);
			}
			ModeChanged?.Invoke(mode);
		}

		// ------------------------------------------------------------------
		// Level tracking and following
		// ------------------------------------------------------------------

		private void OnLoadLevel(LoadLevelEvent data)
		{
			// Any scene transition means we are leaving our current place.
			SetLocalLocation(string.Empty, string.Empty);
			if (IsHost && data.nextGameState != GameManager.GameState.Level && m_hostLocation.HasLevel)
			{
				m_hostLocation = default;
				m_server.Broadcast(BuildHostLocation(m_hostLocation));
			}
		}

		private void OnLevelLoaded(GameLevelLoaded data)
		{
			RefreshLocalLocation();
		}

		private void RefreshLocalLocation()
		{
			GameManager gameManager = Singleton<GameManager>.Instance;
			if (gameManager == null || gameManager.GetGameState() != GameManager.GameState.Level)
			{
				SetLocalLocation(string.Empty, string.Empty);
				return;
			}
			string scene = gameManager.CurrentSceneName ?? string.Empty;
			string identifier = gameManager.CurrentLevelIdentifier ?? string.Empty;
			if (IsHost)
			{
				m_hostLocation = new HostLocation
				{
					HasLevel = true,
					EpisodeType = (int)gameManager.CurrentEpisodeType,
					EpisodeScene = gameManager.CurrentEpisode ?? string.Empty,
					LevelIndex = gameManager.CurrentLevel,
					Identifier = identifier,
					SceneName = scene
				};
				m_server.Broadcast(BuildHostLocation(m_hostLocation));
			}
			SetLocalLocation(scene, identifier);
		}

		private void SetLocalLocation(string scene, string identifier)
		{
			if (LocalPlayer == null)
			{
				return;
			}
			if (LocalPlayer.SceneName == scene && LocalPlayer.LevelIdentifier == identifier)
			{
				return;
			}
			LocalPlayer.SceneName = scene;
			LocalPlayer.LevelIdentifier = identifier;
			if (IsActive)
			{
				using (NetWriter writer = new NetWriter(NetMessageType.PlayerLocation))
				{
					writer.Write(LocalPlayer.Id).Write(scene).Write(identifier);
					SendToAll(writer.ToArray());
				}
			}
			if (m_hasPendingFollow && LocalPlayer.InLevel && scene == m_pendingFollow.SceneName && identifier == m_pendingFollow.Identifier)
			{
				m_hasPendingFollow = false;
			}
			LocalLocationChanged?.Invoke();
			CurrentMode?.OnLocalLocationChanged();
		}

		private void FollowHost(HostLocation location)
		{
			if (!location.HasLevel)
			{
				m_hasPendingFollow = false;
				AddSystemChat("The host left the level.");
				return;
			}
			m_pendingFollow = location;
			m_hasPendingFollow = true;
			m_followAttempts = 0;
			m_followRetryTime = 0f;
			TryFollow();
		}

		private void TryFollow()
		{
			if (!m_hasPendingFollow || State != SessionState.Connected || LocalPlayer == null)
			{
				return;
			}
			HostLocation target = m_pendingFollow;
			if (LocalPlayer.InLevel && LocalPlayer.SceneName == target.SceneName && LocalPlayer.LevelIdentifier == target.Identifier)
			{
				m_hasPendingFollow = false;
				return;
			}
			GameManager gameManager = Singleton<GameManager>.Instance;
			if (gameManager == null || LevelLoader.IsLoadingLevel())
			{
				m_followRetryTime = Time.realtimeSinceStartup + 1f;
				return;
			}
			if (m_followAttempts >= MaxFollowAttempts)
			{
				m_hasPendingFollow = false;
				AddSystemChat("Could not follow the host into " + target.Identifier + ".");
				return;
			}
			m_followAttempts++;
			m_followRetryTime = Time.realtimeSinceStartup + 20f;
			try
			{
				switch ((GameManager.EpisodeType)target.EpisodeType)
				{
				case GameManager.EpisodeType.Sandbox:
					AddSystemChat("Following the host to " + target.Identifier + " ...");
					gameManager.LoadSandboxLevel(target.Identifier);
					break;
				case GameManager.EpisodeType.Race:
					AddSystemChat("Following the host to " + target.Identifier + " ...");
					gameManager.LoadRaceLevel(target.Identifier);
					break;
				case GameManager.EpisodeType.Normal:
					if (string.IsNullOrEmpty(target.EpisodeScene))
					{
						m_hasPendingFollow = false;
						AddSystemChat("Cannot follow the host into " + target.SceneName + ".");
						break;
					}
					AddSystemChat("Following the host to " + target.Identifier + " ...");
					gameManager.LoadLevelSelectionAndLevel(target.EpisodeScene, target.LevelIndex);
					break;
				default:
					m_hasPendingFollow = false;
					AddSystemChat("Cannot follow the host into " + target.SceneName + ".");
					break;
				}
			}
			catch (Exception ex)
			{
				m_hasPendingFollow = false;
				AddSystemChat("Failed to follow the host: " + ex.Message);
			}
		}

		// ------------------------------------------------------------------
		// Chat
		// ------------------------------------------------------------------

		public void AddSystemChat(string text)
		{
			AddChat(string.Empty, text, isSystem: true);
		}

		private void AddChat(string sender, string text, bool isSystem = false)
		{
			Chat.Add(new ChatLine
			{
				Sender = sender,
				Text = text,
				Time = Time.realtimeSinceStartup,
				IsSystem = isSystem
			});
			if (Chat.Count > MaxChatLines)
			{
				Chat.RemoveRange(0, Chat.Count - MaxChatLines);
			}
			ChatVersion++;
			Debug.Log("[Multiplayer] " + (isSystem ? text : sender + ": " + text));
		}

		// ------------------------------------------------------------------
		// Message builders
		// ------------------------------------------------------------------

		private static byte[] BuildChat(int fromId, string text)
		{
			using (NetWriter writer = new NetWriter(NetMessageType.Chat))
			{
				writer.Write(fromId).Write(text);
				return writer.ToArray();
			}
		}

		private byte[] BuildPlayerList()
		{
			using (NetWriter writer = new NetWriter(NetMessageType.PlayerList))
			{
				writer.Write(m_players.Count);
				foreach (MultiplayerPlayer player in m_players)
				{
					writer.Write(player.Id).Write(player.Name).Write(player.IsHost).Write(player.SceneName).Write(player.LevelIdentifier);
				}
				return writer.ToArray();
			}
		}

		private static byte[] BuildHostLocation(HostLocation location)
		{
			using (NetWriter writer = new NetWriter(NetMessageType.HostLocation))
			{
				writer.Write(location.HasLevel)
					.Write(location.EpisodeType)
					.Write(location.EpisodeScene)
					.Write(location.LevelIndex)
					.Write(location.Identifier)
					.Write(location.SceneName);
				return writer.ToArray();
			}
		}

		private static byte[] BuildModeSelect(MultiplayerModeId id)
		{
			using (NetWriter writer = new NetWriter(NetMessageType.ModeSelect))
			{
				writer.Write((byte)id);
				return writer.ToArray();
			}
		}

		private static bool TryParseAddress(string address, out string host, out int port)
		{
			host = null;
			port = NetProtocol.DefaultPort;
			if (string.IsNullOrEmpty(address))
			{
				return false;
			}
			address = address.Trim();
			if (address.StartsWith("["))
			{
				int end = address.IndexOf(']');
				if (end < 0)
				{
					return false;
				}
				host = address.Substring(1, end - 1);
				string rest = address.Substring(end + 1);
				if (rest.StartsWith(":") && !int.TryParse(rest.Substring(1), out port))
				{
					return false;
				}
			}
			else
			{
				int colon = address.LastIndexOf(':');
				if (colon >= 0 && address.IndexOf(':') == colon)
				{
					host = address.Substring(0, colon);
					if (!int.TryParse(address.Substring(colon + 1), out port))
					{
						return false;
					}
				}
				else
				{
					host = address;
				}
			}
			return !string.IsNullOrEmpty(host) && port > 0 && port <= 65535;
		}
	}
}
