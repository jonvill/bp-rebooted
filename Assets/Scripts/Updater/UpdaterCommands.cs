using System;

namespace BPRE.Updater
{
	/// <summary>Console command "update": status | check | now | later.</summary>
	public static class UpdaterCommands
	{
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
				handler.RegisterCommand("update", "Game updates: status | check | now | later", new[]
				{
					new RECommandArgDef("Action", "String", "status | check | now | later")
				}, Execute);
			}
			catch (ArgumentException)
			{
			}
			s_registered = true;
		}

		private static void Execute(RECommandArgs args)
		{
			BuildInfo build = BuildInfo.Current;
			GameUpdater updater = GameUpdater.Instance;
			string action = args.HasValue(0) ? args.GetLowNormString(0) : "status";
			Console.WriteLine("Build " + build.version + " (" + build.build + "), channel " + build.channel);
			if (updater == null)
			{
				Console.WriteLine("Updater not running.");
				return;
			}
			switch (action)
			{
			case "check":
				updater.CheckNow();
				Console.WriteLine("Checking " + build.updateUrl + "latest.json ...");
				break;
			case "now":
				updater.InstallNow();
				Console.WriteLine(updater.StatusText);
				break;
			case "later":
				updater.Postpone();
				Console.WriteLine(updater.StatusText);
				break;
			default:
				Console.WriteLine(updater.State + ": " + updater.StatusText);
				break;
			}
		}
	}
}
