using System;
using System.Text;

namespace BPRE.Multiplayer
{
	/// <summary>
	/// Registers the "mp" command in the in-game command interface once it exists.
	/// </summary>
	public static class MultiplayerCommands
	{
		private const string Usage = "host [port] | join <address[:port]> | leave | mode <freeplay|distance|ctf|battle> | name <name> | say <text> | status | ui";

		private static bool s_registered;

		public static void TryRegister()
		{
			if (s_registered)
			{
				return;
			}
			INAppInterface app = INAppInterface.Instance;
			if (app == null || app.ExecutorDummy == null)
			{
				return;
			}
			RECommandHandler handler = app.ExecutorDummy.GetComponent<RECommandHandler>();
			if (handler == null)
			{
				return;
			}
			try
			{
				handler.RegisterCommand("mp", "Multiplayer: host, join, leave, mode, name, say, status, ui", new[]
				{
					new RECommandArgDef("Action", "String", Usage),
					new RECommandArgDef("Value", "String", "Argument for the action (optional)")
				}, Execute);
			}
			catch (ArgumentException)
			{
				// Already registered (e.g. after a domain reload).
			}
			s_registered = true;
		}

		private static void Execute(RECommandArgs args)
		{
			MultiplayerSession session = MultiplayerSession.Instance;
			if (session == null)
			{
				Console.WriteLine("Multiplayer is not initialised.");
				return;
			}
			string action = args.HasValue(0) ? args.GetLowNormString(0) : "status";
			switch (action)
			{
			case "host":
			{
				int port = session.LastHostPort;
				if (args.HasValue(1) && !int.TryParse(args.GetString(1), out port))
				{
					Console.WriteLine("Invalid port: " + args.GetString(1));
					return;
				}
				Console.WriteLine(session.Host(port) ? session.StatusText : session.LastError);
				break;
			}
			case "join":
			{
				if (!args.HasValue(1))
				{
					Console.WriteLine("Usage: mp join <address[:port]>");
					return;
				}
				Console.WriteLine(session.Join(args.GetString(1)) ? session.StatusText : session.LastError);
				break;
			}
			case "leave":
			case "disconnect":
			case "stop":
				session.Leave();
				Console.WriteLine("Left the multiplayer session.");
				break;
			case "mode":
			{
				if (!args.HasValue(1))
				{
					Console.WriteLine("Current mode: " + MultiplayerGameMode.GetDisplayName(session.CurrentModeId));
					return;
				}
				if (!TryParseMode(args.GetLowNormString(1), out MultiplayerModeId mode))
				{
					Console.WriteLine("Unknown mode. Use: freeplay | distance | ctf | battle");
					return;
				}
				if (session.SelectMode(mode))
				{
					Console.WriteLine("Mode: " + MultiplayerGameMode.GetDisplayName(mode));
				}
				else
				{
					Console.WriteLine("Only the host can change the mode.");
				}
				break;
			}
			case "name":
				if (!args.HasValue(1))
				{
					Console.WriteLine("Your name is " + session.PlayerName);
					return;
				}
				session.PlayerName = JoinRest(args, 1);
				Console.WriteLine("Name set to " + session.PlayerName + " (applies to the next session).");
				break;
			case "say":
			case "chat":
			{
				string text = JoinRest(args, 1);
				if (string.IsNullOrEmpty(text))
				{
					Console.WriteLine("Usage: mp say <text>");
					return;
				}
				if (!session.IsActive)
				{
					Console.WriteLine("Not in a session.");
					return;
				}
				session.SendChat(text);
				break;
			}
			case "ui":
			case "window":
				if (MultiplayerUI.Instance != null)
				{
					MultiplayerUI.Instance.Toggle();
				}
				break;
			case "status":
			case "list":
			case "players":
			{
				Console.WriteLine(session.StatusText + " - mode: " + MultiplayerGameMode.GetDisplayName(session.CurrentModeId));
				foreach (MultiplayerPlayer player in session.Players)
				{
					Console.WriteLine("  " + player.Name + (player.IsHost ? " [host]" : string.Empty) + (player.IsLocal ? " (you)" : string.Empty) + " - " + player.LocationLabel);
				}
				if (!string.IsNullOrEmpty(session.LastError))
				{
					Console.WriteLine("Last error: " + session.LastError);
				}
				break;
			}
			default:
				Console.WriteLine("Unknown action '" + action + "'. Use: " + Usage);
				break;
			}
		}

		private static bool TryParseMode(string text, out MultiplayerModeId mode)
		{
			switch (text)
			{
			case "free":
			case "freeplay":
			case "sandbox":
				mode = MultiplayerModeId.FreePlay;
				return true;
			case "distance":
			case "flight":
			case "far":
				mode = MultiplayerModeId.Distance;
				return true;
			case "ctf":
			case "flag":
			case "capturetheflag":
				mode = MultiplayerModeId.CaptureTheFlag;
				return true;
			case "battle":
			case "fight":
			case "war":
				mode = MultiplayerModeId.Battle;
				return true;
			default:
				mode = MultiplayerModeId.FreePlay;
				return false;
			}
		}

		private static string JoinRest(RECommandArgs args, int from)
		{
			StringBuilder sb = new StringBuilder();
			for (int i = from; args.HasValue(i); i++)
			{
				if (sb.Length > 0)
				{
					sb.Append(' ');
				}
				sb.Append(args.GetString(i));
			}
			return sb.ToString();
		}
	}
}
