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

		private Vector2 m_bodyScroll;

		private string m_nameField = string.Empty;

		private string m_portField = string.Empty;

		private string m_addressField = string.Empty;

		private string m_chatField = string.Empty;

		private enum Popup
		{
			None,
			Address,
			Password
		}

		private bool m_hostTab;

		private bool m_showAdvanced;

		private string m_serverNameField = string.Empty;

		private string m_hostPasswordField = string.Empty;

		private string m_joinPasswordField = string.Empty;

		private Popup m_popup;

		private string m_popupTarget = string.Empty;

		private string m_popupTitle = string.Empty;

		private readonly Dictionary<KeyCode, int> m_lastHotkeyFrame = new Dictionary<KeyCode, int>();

		private Vector2 m_serverListScroll;

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
				m_serverNameField = m_session.PreferredServerName;
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
			if (Input.GetKeyDown(KeyCode.F5))
			{
				HandleHotkey(KeyCode.F5);
			}
			if (Input.GetKeyDown(KeyCode.F4))
			{
				HandleHotkey(KeyCode.F4);
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
			// Rainbow rocket car works in every level, with or without multiplayer.
			if (key == KeyCode.F5 || key == KeyCode.F4)
			{
				HandleRainbowHotkey(key);
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

		private void HandleRainbowHotkey(KeyCode key)
		{
			LevelManager levelManager = WPFMonoBehaviour.levelManager;
			if (levelManager == null || levelManager.ConstructionUI == null)
			{
				return;
			}
			if (key == KeyCode.F4)
			{
				QuickVehicle.FireRockets();
				return;
			}
			if (levelManager.gameState != LevelManager.GameState.Building)
			{
				// Second press: back to building, so F5 always gives a fresh car.
				QuickVehicle.Stop();
				return;
			}
			bool built = QuickVehicle.BuildRainbow(out string error);
			if (built)
			{
				QuickVehicle.StartAndDrive(this);
			}
			if (m_session != null && m_session.IsActive)
			{
				m_session.AddSystemChat(built ? "Rainbow rocket car! F4 fires the rockets, F5 again to rebuild." : error);
			}
			else if (!built)
			{
				Debug.LogWarning("[RainbowRocket] " + error);
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
				if (code == ToggleKey || code == KeyCode.F4 || code == KeyCode.F5 || code == KeyCode.F6 || code == KeyCode.F7 || code == KeyCode.F8)
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
				float width = Mathf.Min(Screen.width - 16f, 440f * Scale);
				float height = Mathf.Min(Screen.height - 16f, (m_session.IsActive ? 560f : 440f) * Scale);
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
			// Small close button in the title bar instead of a big one at the bottom.
			float closeSize = 22f * Scale;
			if (GUI.Button(new Rect(m_windowRect.width - closeSize - 4f * Scale, 2f * Scale, closeSize, closeSize), "X", ButtonStyle))
			{
				SetOpen(false);
			}
			GUILayout.BeginVertical();
			if (m_popup != Popup.None && m_session.State == MultiplayerSession.SessionState.Offline)
			{
				DrawPopup();
			}
			else
			{
				switch (m_session.State)
				{
				case MultiplayerSession.SessionState.Offline:
					DrawOffline();
					break;
				case MultiplayerSession.SessionState.Connecting:
					DrawConnecting();
					break;
				default:
					DrawActive();
					break;
				}
			}
			GUILayout.EndVertical();
			GUI.DragWindow(new Rect(0f, 0f, 10000f, 24f * Scale));
		}

		private void DrawOffline()
		{
			// A wrong or missing password sends us back offline; ask for it right away.
			if (!string.IsNullOrEmpty(m_session.NeedsPasswordFor))
			{
				OpenPasswordPopup(m_session.NeedsPasswordFor, m_session.NeedsPasswordFor);
				m_session.NeedsPasswordFor = null;
				return;
			}

			GUILayout.BeginHorizontal();
			GUILayout.Label("Name", Label, GUILayout.Width(60f * Scale));
			m_nameField = GUILayout.TextField(m_nameField, NetProtocol.MaxNameLength, TextField);
			GUILayout.Space(26f * Scale);
			GUILayout.EndHorizontal();
			GUILayout.Space(6f * Scale);

			GUILayout.BeginHorizontal();
			if (GUILayout.Button("Join", !m_hostTab ? m_modeButtonActive : ButtonStyle))
			{
				m_hostTab = false;
			}
			if (GUILayout.Button("Host", m_hostTab ? m_modeButtonActive : ButtonStyle))
			{
				m_hostTab = true;
			}
			GUILayout.EndHorizontal();
			GUILayout.Space(4f * Scale);

			if (m_hostTab)
			{
				DrawHostTab();
			}
			else
			{
				DrawJoinTab();
			}

			if (!string.IsNullOrEmpty(m_session.LastError))
			{
				GUILayout.Label(m_session.LastError, Error);
			}
		}

		private void DrawJoinTab()
		{
			List<DiscoveredHost> hosts = m_session.DiscoveredHosts;
			m_serverListScroll = GUILayout.BeginScrollView(m_serverListScroll, Box, GUILayout.ExpandHeight(true));
			if (hosts.Count == 0)
			{
				string searching = "Looking for games" + new string('.', 1 + (int)(Time.realtimeSinceStartup * 2f) % 3);
				if (!string.IsNullOrEmpty(m_session.DiscoveryError))
				{
					searching = "Search unavailable: " + m_session.DiscoveryError;
				}
				GUILayout.Label(searching, SystemChat);
			}
			foreach (DiscoveredHost host in hosts)
			{
				HostAnnouncement info = host.Info;
				bool full = info.Players >= info.MaxPlayers;
				GUILayout.BeginHorizontal(Box);
				GUILayout.BeginVertical();
				GUILayout.Label((info.HasPassword ? "[locked] " : string.Empty) + info.HostName, Bold);
				GUILayout.Label(MultiplayerGameMode.GetDisplayName(info.Mode) + "  -  " + info.Players + "/" + info.MaxPlayers + " players" + (host.IsVpn ? "  -  VPN" : string.Empty), Label);
				GUILayout.EndVertical();
				GUI.enabled = !full;
				if (GUILayout.Button(full ? "Full" : "Join", ButtonStyle, GUILayout.Width(80f * Scale), GUILayout.Height(40f * Scale)))
				{
					ApplyName();
					m_addressField = host.Address;
					if (info.HasPassword)
					{
						OpenPasswordPopup(host.Address, info.HostName);
					}
					else
					{
						m_session.Join(host.Address);
					}
				}
				GUI.enabled = true;
				GUILayout.EndHorizontal();
			}
			GUILayout.EndScrollView();
			GUILayout.BeginHorizontal();
			GUILayout.FlexibleSpace();
			if (GUILayout.Button("Join by address...", ButtonStyle))
			{
				m_joinPasswordField = string.Empty;
				m_popup = Popup.Address;
			}
			GUILayout.EndHorizontal();
		}

		private void DrawHostTab()
		{
			GUILayout.BeginHorizontal();
			GUILayout.Label("Server", Label, GUILayout.Width(90f * Scale));
			m_serverNameField = GUILayout.TextField(m_serverNameField, NetProtocol.MaxServerNameLength, TextField);
			GUILayout.EndHorizontal();
			GUILayout.BeginHorizontal();
			GUILayout.Label("Password", Label, GUILayout.Width(90f * Scale));
			m_hostPasswordField = GUILayout.PasswordField(m_hostPasswordField, '*', NetProtocol.MaxPasswordLength, TextField);
			GUILayout.EndHorizontal();
			GUILayout.Label(string.IsNullOrEmpty(m_hostPasswordField) ? "No password: anyone can join." : "Only players with the password can join.", SystemChat);
			GUILayout.Space(4f * Scale);
			DrawModeSelector();
			GUILayout.Space(4f * Scale);
			m_showAdvanced = GUILayout.Toggle(m_showAdvanced, "Advanced", ToggleStyle);
			if (m_showAdvanced)
			{
				GUILayout.BeginHorizontal();
				GUILayout.Label("Port", Label, GUILayout.Width(90f * Scale));
				m_portField = GUILayout.TextField(m_portField, 5, TextField, GUILayout.Width(90f * Scale));
				GUILayout.FlexibleSpace();
				GUILayout.EndHorizontal();
			}
			GUILayout.FlexibleSpace();
			if (GUILayout.Button("Start server", m_modeButtonActive, GUILayout.Height(36f * Scale)))
			{
				ApplyName();
				if (!int.TryParse(m_portField, out int port))
				{
					port = NetProtocol.DefaultPort;
				}
				m_session.PreferredServerName = m_serverNameField;
				m_session.Host(port, m_serverNameField, m_hostPasswordField);
			}
		}

		private void OpenPasswordPopup(string address, string title)
		{
			// After a wrong password the session only knows the address; keep the server name we showed.
			if (address != m_popupTarget || string.IsNullOrEmpty(m_popupTitle))
			{
				m_popupTitle = title;
			}
			m_popupTarget = address;
			m_joinPasswordField = string.Empty;
			m_popup = Popup.Password;
		}

		private void DrawPopup()
		{
			bool submit = false;
			UnityEngine.Event current = UnityEngine.Event.current;
			if (current.type == UnityEngine.EventType.KeyDown && (current.keyCode == KeyCode.Return || current.keyCode == KeyCode.KeypadEnter))
			{
				submit = true;
				current.Use();
			}
			GUILayout.BeginVertical(Box);
			if (m_popup == Popup.Address)
			{
				GUILayout.Label("Join by address", Bold);
				GUILayout.BeginHorizontal();
				GUILayout.Label("Address", Label, GUILayout.Width(90f * Scale));
				m_addressField = GUILayout.TextField(m_addressField, 64, TextField);
				GUILayout.EndHorizontal();
			}
			else
			{
				GUILayout.Label("Password for " + m_popupTitle, Bold);
			}
			GUILayout.BeginHorizontal();
			GUILayout.Label("Password", Label, GUILayout.Width(90f * Scale));
			m_joinPasswordField = GUILayout.PasswordField(m_joinPasswordField, '*', NetProtocol.MaxPasswordLength, TextField);
			GUILayout.EndHorizontal();
			if (!string.IsNullOrEmpty(m_session.LastError))
			{
				GUILayout.Label(m_session.LastError, Error);
			}
			GUILayout.BeginHorizontal();
			if (GUILayout.Button("Cancel", ButtonStyle))
			{
				m_popup = Popup.None;
				submit = false;
			}
			if (GUILayout.Button("Join", m_modeButtonActive))
			{
				submit = true;
			}
			GUILayout.EndHorizontal();
			GUILayout.EndVertical();
			if (submit && m_popup != Popup.None)
			{
				string address = m_popup == Popup.Address ? m_addressField : m_popupTarget;
				ApplyName();
				m_popup = Popup.None;
				m_session.Join(address, m_joinPasswordField);
			}
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
			GUILayout.Label((m_session.HasPassword ? "[locked] " : string.Empty) + m_session.ServerName + "  -  " + m_session.Players.Count + "/" + NetProtocol.MaxPlayers, Bold);
			if (GUILayout.Button("Leave", ButtonStyle, GUILayout.Width(80f * Scale)))
			{
				m_session.Leave();
				GUILayout.EndHorizontal();
				return;
			}
			GUILayout.Space(26f * Scale);
			GUILayout.EndHorizontal();

			m_bodyScroll = GUILayout.BeginScrollView(m_bodyScroll, GUILayout.ExpandHeight(true));

			MultiplayerGameMode mode = m_session.CurrentMode;
			GUILayout.BeginVertical(Box);
			if (m_session.IsHost)
			{
				DrawModeSelector();
			}
			else
			{
				GUILayout.Label("Mode: " + (mode != null ? mode.DisplayName : "-"), Bold);
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

			GUILayout.BeginVertical(Box);
			MultiplayerPlayer kick = null;
			bool ban = false;
			foreach (MultiplayerPlayer player in m_session.Players)
			{
				GUILayout.BeginHorizontal();
				string line = player.Name + (player.IsHost ? " (host)" : string.Empty) + (player.IsLocal ? " (you)" : string.Empty) + "  -  " + player.LocationLabel;
				GUILayout.Label(line, Label);
				if (m_session.IsHost && !player.IsLocal)
				{
					if (GUILayout.Button("Kick", ButtonStyle, GUILayout.Width(56f * Scale)))
					{
						kick = player;
					}
					if (GUILayout.Button("Ban", ButtonStyle, GUILayout.Width(56f * Scale)))
					{
						kick = player;
						ban = true;
					}
				}
				GUILayout.EndHorizontal();
			}
			if (m_session.IsHost && m_session.BannedCount > 0)
			{
				GUILayout.BeginHorizontal();
				GUILayout.Label(m_session.BannedCount + " banned", SystemChat);
				if (GUILayout.Button("Unban all", ButtonStyle, GUILayout.Width(110f * Scale)))
				{
					m_session.ClearBans();
				}
				GUILayout.EndHorizontal();
			}
			GUILayout.EndVertical();
			if (kick != null)
			{
				m_session.KickPlayer(kick.Id, ban);
			}

			if (m_seenChatVersion != m_session.ChatVersion)
			{
				m_seenChatVersion = m_session.ChatVersion;
				m_chatScroll.y = float.MaxValue;
			}
			m_chatScroll = GUILayout.BeginScrollView(m_chatScroll, Box, GUILayout.Height(110f * Scale));
			foreach (MultiplayerSession.ChatLine line in m_session.Chat)
			{
				GUILayout.Label(FormatChatLine(line), line.IsSystem ? SystemChat : Label);
			}
			GUILayout.EndScrollView();

			GUILayout.EndScrollView();

			// The input row stays outside the scroll area so it is always reachable.
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
			if (GUILayout.Button("Send", ButtonStyle, GUILayout.Width(70f * Scale)))
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
			if (m_session.LocalPlayer != null && m_session.LocalPlayer.InLevel)
			{
				GUILayout.Label("Starter car: F7 build - F8 start/stop - F6 reverse", SystemChat);
			}
		}

		private void ApplyName()
		{
			m_session.PlayerName = m_nameField;
			m_nameField = m_session.PlayerName;
		}
	}
}
