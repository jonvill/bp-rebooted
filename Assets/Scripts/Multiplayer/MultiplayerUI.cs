using System.Collections.Generic;
using UnityEngine;

namespace BPRE.Multiplayer
{
	/// <summary>
	/// IMGUI overlay: session window (toggle with F9 or the "mp ui" command),
	/// a small status line, recent chat, mode HUD and name tags above remote contraptions.
	/// </summary>
	public sealed class MultiplayerUI : MonoBehaviour
	{
		private const int WindowId = 7771;

		private const string ChatControlName = "bpre_mp_chat";

		private const float HudChatDuration = 8f;

		private const int HudChatLines = 5;

		public static MultiplayerUI Instance { get; private set; }

		public KeyCode ToggleKey = KeyCode.F9;

		public bool IsOpen { get; private set; }

		// Styles shared with game modes.
		public float Scale { get; private set; } = 1f;

		public GUIStyle Label { get; private set; }

		public GUIStyle Bold { get; private set; }

		public GUIStyle Error { get; private set; }

		public GUIStyle SystemChat { get; private set; }

		public GUIStyle ButtonStyle { get; private set; }

		public GUIStyle TextField { get; private set; }

		public GUIStyle Box { get; private set; }

		public GUIStyle ToggleStyle { get; private set; }

		public GUIStyle Hud { get; private set; }

		public GUIStyle HudBold { get; private set; }

		public GUIStyle HudAlert { get; private set; }

		private MultiplayerSession m_session;

		private Rect m_windowRect;

		private Vector2 m_chatScroll;

		private Vector2 m_playerScroll;

		private Vector2 m_bodyScroll;

		private string m_nameField = string.Empty;

		private string m_portField = string.Empty;

		private string m_addressField = string.Empty;

		private string m_chatField = string.Empty;

		private string m_vehicleMessage = string.Empty;

		private readonly Dictionary<KeyCode, int> m_lastHotkeyFrame = new Dictionary<KeyCode, int>();

		private Vector2 m_serverListScroll;

		private Vector2 m_offlineScroll;

		private GameObject m_blockerRoot;

		private RectTransform m_blockerRect;

		private int m_seenChatVersion = -1;

		private bool m_guiDisabledByUs;

		private bool m_stylesReady;

		private GUIStyle m_windowStyle;

		private GUIStyle m_nameTagStyle;

		private GUIStyle m_nameTagShadowStyle;

		private GUIStyle m_modeButtonActive;

		private void Awake()
		{
			Instance = this;
		}

		private void Start()
		{
			m_session = MultiplayerSession.Instance;
			if (m_session != null)
			{
				m_nameField = m_session.PlayerName;
				m_portField = m_session.LastHostPort.ToString();
				m_addressField = m_session.LastJoinAddress;
			}
		}

		private void OnDestroy()
		{
			SetOpen(false);
			if (m_blockerRoot != null)
			{
				Destroy(m_blockerRoot);
			}
			if (Instance == this)
			{
				Instance = null;
			}
		}

		/// <summary>
		/// The window is drawn with IMGUI, but the mod's own buttons are uGUI. Without a blocker,
		/// clicks on the window also press whatever uGUI button lies underneath it.
		/// </summary>
		private void UpdateInputBlocker()
		{
			if (!IsOpen)
			{
				if (m_blockerRoot != null && m_blockerRoot.activeSelf)
				{
					m_blockerRoot.SetActive(false);
				}
				return;
			}
			if (m_blockerRoot == null)
			{
				m_blockerRoot = new GameObject("BPRE_MultiplayerInputBlocker");
				DontDestroyOnLoad(m_blockerRoot);
				Canvas canvas = m_blockerRoot.AddComponent<Canvas>();
				canvas.renderMode = RenderMode.ScreenSpaceOverlay;
				canvas.sortingOrder = 32000;
				m_blockerRoot.AddComponent<UnityEngine.UI.GraphicRaycaster>();
				GameObject panel = new GameObject("Blocker", typeof(RectTransform));
				panel.transform.SetParent(m_blockerRoot.transform, false);
				UnityEngine.UI.Image image = panel.AddComponent<UnityEngine.UI.Image>();
				image.color = new Color(0f, 0f, 0f, 0f);
				image.raycastTarget = true;
				m_blockerRect = panel.GetComponent<RectTransform>();
				m_blockerRect.anchorMin = Vector2.zero;
				m_blockerRect.anchorMax = Vector2.zero;
				m_blockerRect.pivot = Vector2.zero;
			}
			if (!m_blockerRoot.activeSelf)
			{
				m_blockerRoot.SetActive(true);
			}
			// IMGUI uses a top-left origin, uGUI a bottom-left one.
			m_blockerRect.anchoredPosition = new Vector2(m_windowRect.x, Screen.height - m_windowRect.y - m_windowRect.height);
			m_blockerRect.sizeDelta = new Vector2(m_windowRect.width, m_windowRect.height);
		}

		private void Update()
		{
			if (Input.GetKeyDown(ToggleKey))
			{
				HandleHotkey(ToggleKey);
			}
			if (Input.GetKeyDown(KeyCode.F7))
			{
				HandleHotkey(KeyCode.F7);
			}
			if (Input.GetKeyDown(KeyCode.F8))
			{
				HandleHotkey(KeyCode.F8);
			}
			if (Input.GetKeyDown(KeyCode.F6))
			{
				HandleHotkey(KeyCode.F6);
			}
			UpdateInputBlocker();
			// Search for LAN games only while someone looks at the join list.
			if (m_session != null)
			{
				m_session.SetBrowsing(IsOpen && m_session.State == MultiplayerSession.SessionState.Offline);
			}
		}

		/// <summary>
		/// Runs a hotkey once per frame. Called from Update (Input) and from OnGUI (IMGUI events),
		/// because while a text field of this window has keyboard focus the key only reaches OnGUI.
		/// </summary>
		private void HandleHotkey(KeyCode key)
		{
			int frame = Time.frameCount;
			if (m_lastHotkeyFrame.TryGetValue(key, out int last) && last == frame)
			{
				return;
			}
			m_lastHotkeyFrame[key] = frame;
			if (key == ToggleKey)
			{
				Toggle();
				return;
			}
			// Starter car shortcuts only while in a session and inside a level.
			if (m_session == null || !m_session.IsActive || m_session.LocalPlayer == null || !m_session.LocalPlayer.InLevel)
			{
				return;
			}
			switch (key)
			{
			case KeyCode.F7:
				m_session.AddSystemChat(QuickVehicle.Build(out string error) ? "Starter car placed. F8 starts it, F6 reverses." : error);
				break;
			case KeyCode.F8:
				if (QuickVehicle.IsRunning)
				{
					QuickVehicle.Stop();
				}
				else if (QuickVehicle.CanBuild)
				{
					QuickVehicle.StartAndDrive(this);
				}
				break;
			case KeyCode.F6:
				QuickVehicle.ToggleReverse();
				break;
			}
		}

		public void Toggle()
		{
			SetOpen(!IsOpen);
		}

		public void SetOpen(bool open)
		{
			if (IsOpen == open)
			{
				return;
			}
			IsOpen = open;
			if (!open)
			{
				// Release text field focus so the game gets keyboard input again.
				GUIUtility.keyboardControl = 0;
			}
			UpdateInputBlocker();
			try
			{
				GuiManager guiManager = Singleton<GuiManager>.Instance;
				if (guiManager != null)
				{
					if (open && guiManager.IsEnabled)
					{
						guiManager.IsEnabled = false;
						m_guiDisabledByUs = true;
					}
					else if (!open && m_guiDisabledByUs)
					{
						guiManager.IsEnabled = true;
						m_guiDisabledByUs = false;
					}
				}
			}
			catch
			{
				m_guiDisabledByUs = false;
			}
		}

		private void OnGUI()
		{
			if (m_session == null)
			{
				return;
			}
			EnsureStyles();
			UnityEngine.Event keyEvent = UnityEngine.Event.current;
			if (keyEvent.type == UnityEngine.EventType.KeyDown)
			{
				KeyCode code = keyEvent.keyCode;
				if (code == ToggleKey || code == KeyCode.F6 || code == KeyCode.F7 || code == KeyCode.F8)
				{
					HandleHotkey(code);
					keyEvent.Use();
				}
			}
			if (m_session.IsActive)
			{
				DrawNameTags();
				try
				{
					m_session.CurrentMode?.DrawHud(this);
				}
				catch (System.Exception ex)
				{
					Debug.LogWarning("[Multiplayer] Mode HUD failed: " + ex.Message);
				}
			}
			DrawHud();
			if (IsOpen)
			{
				float width = Mathf.Min(Screen.width - 16f, 480f * Scale);
				float height = Mathf.Min(Screen.height - 16f, 600f * Scale);
				if (m_windowRect.width <= 0f)
				{
					m_windowRect = new Rect((Screen.width - width) * 0.5f, (Screen.height - height) * 0.5f, width, height);
				}
				m_windowRect.width = width;
				m_windowRect.height = height;
				m_windowRect = GUILayout.Window(WindowId, m_windowRect, DrawWindow, "Multiplayer", m_windowStyle);
				m_windowRect.x = Mathf.Clamp(m_windowRect.x, 0f, Mathf.Max(0f, Screen.width - width));
				m_windowRect.y = Mathf.Clamp(m_windowRect.y, 0f, Mathf.Max(0f, Screen.height - height));
			}
		}

		private void EnsureStyles()
		{
			float scale = Mathf.Max(1f, Screen.height / 720f);
			if (m_stylesReady && Mathf.Approximately(scale, Scale))
			{
				return;
			}
			Scale = scale;
			int fontSize = Mathf.RoundToInt(14f * scale);
			m_windowStyle = new GUIStyle(GUI.skin.window) { fontSize = fontSize };
			Label = new GUIStyle(GUI.skin.label) { fontSize = fontSize, wordWrap = true };
			Bold = new GUIStyle(Label) { fontStyle = FontStyle.Bold };
			Error = new GUIStyle(Label);
			Error.normal.textColor = new Color(1f, 0.45f, 0.4f);
			SystemChat = new GUIStyle(Label);
			SystemChat.normal.textColor = new Color(0.75f, 0.85f, 1f);
			ButtonStyle = new GUIStyle(GUI.skin.button) { fontSize = fontSize };
			m_modeButtonActive = new GUIStyle(ButtonStyle) { fontStyle = FontStyle.Bold };
			m_modeButtonActive.normal = m_modeButtonActive.active;
			TextField = new GUIStyle(GUI.skin.textField) { fontSize = fontSize };
			Box = new GUIStyle(GUI.skin.box) { fontSize = fontSize };
			ToggleStyle = new GUIStyle(GUI.skin.toggle) { fontSize = fontSize };
			m_nameTagStyle = new GUIStyle(GUI.skin.label)
			{
				fontSize = Mathf.RoundToInt(15f * scale),
				alignment = TextAnchor.MiddleCenter,
				fontStyle = FontStyle.Bold
			};
			m_nameTagStyle.normal.textColor = Color.white;
			m_nameTagShadowStyle = new GUIStyle(m_nameTagStyle);
			m_nameTagShadowStyle.normal.textColor = Color.black;
			Hud = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(13f * scale) };
			Hud.normal.textColor = Color.white;
			HudBold = new GUIStyle(Hud) { fontStyle = FontStyle.Bold, fontSize = Mathf.RoundToInt(15f * scale) };
			HudBold.normal.textColor = Color.white;
			HudAlert = new GUIStyle(HudBold);
			HudAlert.normal.textColor = new Color(1f, 0.4f, 0.35f);
			m_stylesReady = true;
		}

		// ------------------------------------------------------------------
		// Helpers for modes
		// ------------------------------------------------------------------

		public void DrawShadowedLabel(Rect rect, string text, GUIStyle style)
		{
			Color color = style.normal.textColor;
			style.normal.textColor = Color.black;
			GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), text, style);
			style.normal.textColor = color;
			GUI.Label(rect, text, style);
		}

		/// <summary>Draws a centred label at a world position (in-game overlay).</summary>
		public void DrawWorldLabel(Vector3 worldPosition, string text)
		{
			Camera camera = Camera.main;
			if (camera == null)
			{
				return;
			}
			Vector3 screen = camera.WorldToScreenPoint(worldPosition);
			if (screen.z < 0f)
			{
				return;
			}
			float width = 220f * Scale;
			float height = 24f * Scale;
			Rect rect = new Rect(screen.x - width * 0.5f, Screen.height - screen.y - height, width, height);
			GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), text, m_nameTagShadowStyle);
			GUI.Label(rect, text, m_nameTagStyle);
		}

		// ------------------------------------------------------------------
		// Name tags and HUD
		// ------------------------------------------------------------------

		private void DrawNameTags()
		{
			if (ContraptionSync.Instance == null)
			{
				return;
			}
			foreach (RemoteContraption ghost in ContraptionSync.Instance.Ghosts)
			{
				if (ghost.TryGetLabelPosition(out Vector3 worldPosition))
				{
					DrawWorldLabel(worldPosition, ghost.Player.Name);
				}
			}
		}

		private void DrawHud()
		{
			if (IsOpen)
			{
				return;
			}
			float x = 8f * Scale;
			float y = 8f * Scale;
			float lineHeight = 18f * Scale;
			float width = 520f * Scale;
			if (m_session.State == MultiplayerSession.SessionState.Offline)
			{
				// Discreet hint in the main menu so people can find the feature.
				GameManager gameManager = Singleton<GameManager>.Instance;
				if (gameManager != null && gameManager.GetGameState() == GameManager.GameState.MainMenu)
				{
					float hintHeight = 20f * Scale;
					DrawShadowedLabel(new Rect(8f * Scale, Screen.height - hintHeight - 6f * Scale, 400f * Scale, hintHeight), "[" + ToggleKey + "] Multiplayer", Hud);
				}
				// Keep showing recent messages, so an unexpected disconnect is visible without opening the window.
			}
			else
			{
				string status = "[" + ToggleKey + "] " + m_session.StatusText;
				if (m_session.CurrentMode != null)
				{
					status += "  -  " + m_session.CurrentMode.DisplayName;
				}
				DrawShadowedLabel(new Rect(x, y, width, lineHeight), status, Hud);
				y += lineHeight;
			}
			List<MultiplayerSession.ChatLine> chat = m_session.Chat;
			for (int i = Mathf.Max(0, chat.Count - HudChatLines); i < chat.Count; i++)
			{
				MultiplayerSession.ChatLine line = chat[i];
				if (Time.realtimeSinceStartup - line.Time > HudChatDuration)
				{
					continue;
				}
				DrawShadowedLabel(new Rect(x, y, width, lineHeight), FormatChatLine(line), Hud);
				y += lineHeight;
			}
		}

		private static string FormatChatLine(MultiplayerSession.ChatLine line)
		{
			return line.IsSystem ? "* " + line.Text : line.Sender + ": " + line.Text;
		}

		// ------------------------------------------------------------------
		// Window
		// ------------------------------------------------------------------

		private void DrawWindow(int id)
		{
			GUILayout.BeginVertical();
			switch (m_session.State)
			{
			case MultiplayerSession.SessionState.Offline:
				m_offlineScroll = GUILayout.BeginScrollView(m_offlineScroll, GUILayout.ExpandHeight(true));
				DrawOffline();
				GUILayout.EndScrollView();
				break;
			case MultiplayerSession.SessionState.Connecting:
				DrawConnecting();
				break;
			default:
				DrawActive();
				break;
			}
			GUILayout.FlexibleSpace();
			if (GUILayout.Button("Close (" + ToggleKey + ")", ButtonStyle))
			{
				SetOpen(false);
			}
			GUILayout.EndVertical();
			GUI.DragWindow(new Rect(0f, 0f, 10000f, 24f * Scale));
		}

		private void DrawOffline()
		{
			GUILayout.Label("Your name", Bold);
			m_nameField = GUILayout.TextField(m_nameField, NetProtocol.MaxNameLength, TextField);
			GUILayout.Space(8f * Scale);

			GUILayout.BeginVertical(Box);
			GUILayout.Label("Host a game", Bold);
			GUILayout.BeginHorizontal();
			GUILayout.Label("Port", Label, GUILayout.Width(50f * Scale));
			m_portField = GUILayout.TextField(m_portField, 5, TextField, GUILayout.Width(90f * Scale));
			GUILayout.FlexibleSpace();
			if (GUILayout.Button("Host", ButtonStyle, GUILayout.Width(120f * Scale)))
			{
				ApplyName();
				if (!int.TryParse(m_portField, out int port))
				{
					port = NetProtocol.DefaultPort;
				}
				m_session.Host(port);
			}
			GUILayout.EndHorizontal();
			GUILayout.Label("Game mode", Label);
			DrawModeSelector();
			GUILayout.EndVertical();
			GUILayout.Space(8f * Scale);

			GUILayout.BeginVertical(Box);
			GUILayout.Label("Join a game", Bold);
			GUILayout.Label("Games on your network", Label);
			List<DiscoveredHost> hosts = m_session.DiscoveredHosts;
			if (hosts.Count == 0)
			{
				string searching = m_session.IsBrowsing ? "Searching" + new string('.', 1 + (int)(Time.realtimeSinceStartup * 2f) % 3) : "Search not running";
				if (!string.IsNullOrEmpty(m_session.DiscoveryError))
				{
					searching = "Search unavailable: " + m_session.DiscoveryError;
				}
				GUILayout.Label(searching, SystemChat);
			}
			else
			{
				m_serverListScroll = GUILayout.BeginScrollView(m_serverListScroll, GUILayout.Height(Mathf.Min(hosts.Count, 4) * 30f * Scale + 6f));
				foreach (DiscoveredHost host in hosts)
				{
					HostAnnouncement info = host.Info;
					bool full = info.Players >= info.MaxPlayers;
					GUILayout.BeginHorizontal();
					string line = info.HostName + "  -  " + MultiplayerGameMode.GetDisplayName(info.Mode) + "  -  " + info.Players + "/" + info.MaxPlayers + "  -  " + info.Level + "  (" + host.PingMs + " ms)";
					GUILayout.Label(line, Label);
					GUI.enabled = !full;
					if (GUILayout.Button(full ? "Full" : "Join", ButtonStyle, GUILayout.Width(80f * Scale)))
					{
						ApplyName();
						m_addressField = host.Address;
						m_session.Join(host.Address);
					}
					GUI.enabled = true;
					GUILayout.EndHorizontal();
				}
				GUILayout.EndScrollView();
			}
			GUILayout.Space(4f * Scale);
			GUILayout.Label("Or enter an address (e.g. Tailscale or internet)", Label);
			GUILayout.BeginHorizontal();
			GUILayout.Label("Address", Label, GUILayout.Width(70f * Scale));
			m_addressField = GUILayout.TextField(m_addressField, 64, TextField);
			if (GUILayout.Button("Join", ButtonStyle, GUILayout.Width(120f * Scale)))
			{
				ApplyName();
				m_session.Join(m_addressField);
			}
			GUILayout.EndHorizontal();
			GUILayout.EndVertical();
			GUILayout.Space(8f * Scale);

			if (!string.IsNullOrEmpty(m_session.LastError))
			{
				GUILayout.Label(m_session.LastError, Error);
			}
			GUILayout.Label("The host plays as usual and picks any level; everyone in the session follows automatically. " +
				"Each player builds and drives their own contraption; the others appear as ghosts.", Label);
			GUILayout.Label("Console: mp host [port], mp join <address>, mp leave, mp mode <name>, mp say <text>", Label);
		}

		private void DrawModeSelector()
		{
			MultiplayerModeId current = m_session.CurrentModeId;
			GUILayout.BeginHorizontal();
			foreach (MultiplayerModeId mode in MultiplayerGameMode.AllModes)
			{
				bool active = mode == current;
				if (GUILayout.Button(MultiplayerGameMode.GetDisplayName(mode), active ? m_modeButtonActive : ButtonStyle) && !active)
				{
					m_session.SelectMode(mode);
				}
			}
			GUILayout.EndHorizontal();
		}

		private void DrawConnecting()
		{
			GUILayout.Label(m_session.StatusText, Bold);
			if (GUILayout.Button("Cancel", ButtonStyle))
			{
				m_session.Leave();
			}
		}

		private void DrawActive()
		{
			GUILayout.BeginHorizontal();
			GUILayout.Label(m_session.StatusText, Bold);
			if (GUILayout.Button("Leave", ButtonStyle, GUILayout.Width(100f * Scale)))
			{
				m_session.Leave();
				GUILayout.EndHorizontal();
				return;
			}
			GUILayout.EndHorizontal();

			m_bodyScroll = GUILayout.BeginScrollView(m_bodyScroll, GUILayout.ExpandHeight(true));

			MultiplayerGameMode mode = m_session.CurrentMode;
			GUILayout.BeginVertical(Box);
			GUILayout.Label("Mode: " + (mode != null ? mode.DisplayName : "-"), Bold);
			if (m_session.IsHost)
			{
				DrawModeSelector();
				GUILayout.Label("Load any level and the others will follow.", Label);
			}
			if (mode != null)
			{
				try
				{
					mode.DrawWindow(this);
				}
				catch (System.Exception ex)
				{
					GUILayout.Label("Mode UI error: " + ex.Message, Error);
				}
			}
			GUILayout.EndVertical();

			if (m_session.LocalPlayer != null && m_session.LocalPlayer.InLevel)
			{
				GUILayout.BeginVertical(Box);
				GUILayout.Label("Starter car (frame, pig, engine, gearbox, motor wheels)  -  F7 build, F8 start/stop, F6 reverse", Bold);
				GUILayout.BeginHorizontal();
				GUI.enabled = QuickVehicle.CanBuild;
				if (GUILayout.Button("Build car", ButtonStyle))
				{
					if (!QuickVehicle.Build(out string error))
					{
						m_vehicleMessage = error;
					}
					else
					{
						m_vehicleMessage = "Car placed. Press Start to drive.";
					}
				}
				GUI.enabled = QuickVehicle.CanBuild || QuickVehicle.IsRunning;
				if (GUILayout.Button(QuickVehicle.IsRunning ? "Stop" : "Start + drive", ButtonStyle))
				{
					if (QuickVehicle.IsRunning)
					{
						QuickVehicle.Stop();
					}
					else
					{
						QuickVehicle.StartAndDrive(this);
						SetOpen(false);
					}
					m_vehicleMessage = string.Empty;
				}
				GUI.enabled = QuickVehicle.IsRunning;
				if (GUILayout.Button("Reverse", ButtonStyle))
				{
					QuickVehicle.ToggleReverse();
					SetOpen(false);
				}
				GUI.enabled = true;
				GUILayout.EndHorizontal();
				if (!string.IsNullOrEmpty(m_vehicleMessage))
				{
					GUILayout.Label(m_vehicleMessage, Label);
				}
				GUILayout.EndVertical();
			}

			GUILayout.Label("Players", Bold);
			m_playerScroll = GUILayout.BeginScrollView(m_playerScroll, Box, GUILayout.Height(90f * Scale));
			foreach (MultiplayerPlayer player in m_session.Players)
			{
				string line = player.Name;
				if (player.IsHost)
				{
					line += " [host]";
				}
				if (player.IsLocal)
				{
					line += " (you)";
				}
				line += "  -  " + player.LocationLabel;
				GUILayout.Label(line, Label);
			}
			GUILayout.EndScrollView();

			GUILayout.Label("Chat", Bold);
			if (m_seenChatVersion != m_session.ChatVersion)
			{
				m_seenChatVersion = m_session.ChatVersion;
				m_chatScroll.y = float.MaxValue;
			}
			m_chatScroll = GUILayout.BeginScrollView(m_chatScroll, Box, GUILayout.Height(140f * Scale));
			foreach (MultiplayerSession.ChatLine line in m_session.Chat)
			{
				GUILayout.Label(FormatChatLine(line), line.IsSystem ? SystemChat : Label);
			}
			GUILayout.EndScrollView();

			GUILayout.EndScrollView();

			// The input row stays outside the scroll area so it is always reachable,
			// however much the selected mode adds to the window.
			bool submit = false;
			UnityEngine.Event current = UnityEngine.Event.current;
			if (current.type == UnityEngine.EventType.KeyDown && (current.keyCode == KeyCode.Return || current.keyCode == KeyCode.KeypadEnter) && GUI.GetNameOfFocusedControl() == ChatControlName)
			{
				submit = true;
				current.Use();
			}
			GUILayout.BeginHorizontal();
			GUI.SetNextControlName(ChatControlName);
			m_chatField = GUILayout.TextField(m_chatField, NetProtocol.MaxChatLength, TextField);
			if (GUILayout.Button("Send", ButtonStyle, GUILayout.Width(80f * Scale)))
			{
				submit = true;
			}
			GUILayout.EndHorizontal();
			if (submit && !string.IsNullOrEmpty(m_chatField.Trim()))
			{
				m_session.SendChat(m_chatField);
				m_chatField = string.Empty;
				GUI.FocusControl(ChatControlName);
			}
		}

		private void ApplyName()
		{
			m_session.PlayerName = m_nameField;
			m_nameField = m_session.PlayerName;
		}
	}
}
