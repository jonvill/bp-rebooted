using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace BPRE.Updater
{
	/// <summary>
	/// Over-the-air updates for release builds.
	///
	/// 1. Checks {updateUrl}latest.json at start and every 30 minutes.
	/// 2. If a newer build is published and its manifest signature is valid, downloads the zip
	///    in the background and verifies its SHA-256.
	/// 3. Installs it in the main menu (after a short countdown the player can postpone) or at
	///    the next game start: a small PowerShell script waits for the game to exit, copies the
	///    new files over the installation and starts the game again.
	/// Development builds (no StreamingAssets/bpre_build.json) and the editor never update.
	/// </summary>
	public sealed class GameUpdater : MonoBehaviour
	{
		public enum UpdateState
		{
			Disabled,
			Idle,
			Checking,
			UpToDate,
			Downloading,
			Verifying,
			Ready,
			Installing,
			Failed
		}

		[Serializable]
		private sealed class PendingUpdate
		{
			public int build;

			public string version;

			public string zipPath;

			public string sha256;

			public int attempts;
		}

		private const float CheckInterval = 30f * 60f;

		private const float RestartCountdown = 15f;

		private const float RetryInterval = 60f;

		private const string InstallLockFileName = "installing.lock";

		/// <summary>An install lock older than this is considered stale (e.g. the installer crashed).</summary>
		private const double InstallLockMaxAgeMinutes = 15.0;

		private const string PendingFileName = "pending.json";

		private const string FailedBuildKey = "bpre_update_failed_build";

		public static GameUpdater Instance { get; private set; }

		public UpdateState State { get; private set; } = UpdateState.Idle;

		public string StatusText { get; private set; } = string.Empty;

		public UpdateManifest Available { get; private set; }

		public float DownloadProgress { get; private set; }

		private BuildInfo m_build;

		private string m_updateDir;

		private float m_nextCheck;

		private volatile bool m_workerBusy;

		private string m_workerResultText;

		private string m_workerError;

		private volatile bool m_workerDone;

		private Action m_onWorkerDone;

		private PendingUpdate m_pending;

		private bool m_postponed;

		private float m_countdownEnd = -1f;

		private float m_nextLockCheck;

		private bool m_otherInstanceInstalling;

		private float m_quitForOtherInstallerAt = -1f;

		private GUIStyle m_bannerStyle;

		private GUIStyle m_smallStyle;

		private GUIStyle m_buttonStyle;

		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
		private static void Bootstrap()
		{
			if (Instance != null)
			{
				return;
			}
			GameObject go = new GameObject("BPRE_Updater");
			DontDestroyOnLoad(go);
			go.AddComponent<GameUpdater>();
		}

		private void Awake()
		{
			Instance = this;
			m_build = BuildInfo.Current;
			m_updateDir = Path.Combine(Application.persistentDataPath, "updates");
			if (Application.isEditor || !m_build.IsRelease)
			{
				State = UpdateState.Disabled;
				StatusText = Application.isEditor ? "Updates disabled in the editor." : "Development build - automatic updates are off.";
				return;
			}
			Debug.Log("[Updater] Build " + m_build.version + " (" + m_build.build + "), feed " + m_build.updateUrl);
		}

		private void Start()
		{
			UpdaterCommands.TryRegister();
			if (State == UpdateState.Disabled)
			{
				return;
			}
			try
			{
				Directory.CreateDirectory(m_updateDir);
			}
			catch (Exception ex)
			{
				Fail("Cannot create update folder: " + ex.Message);
				return;
			}
			if (TryInstallPendingAtStartup())
			{
				return;
			}
			m_nextCheck = Time.realtimeSinceStartup + 3f;
		}

		private void Update()
		{
			UpdaterCommands.TryRegister();
			if (State == UpdateState.Disabled)
			{
				return;
			}
			if (m_workerDone)
			{
				m_workerDone = false;
				m_workerBusy = false;
				Action callback = m_onWorkerDone;
				m_onWorkerDone = null;
				callback?.Invoke();
			}
			if (State != UpdateState.Installing && Time.realtimeSinceStartup >= m_nextLockCheck)
			{
				m_nextLockCheck = Time.realtimeSinceStartup + 1f;
				FollowOtherInstaller();
			}
			if (!m_workerBusy && State != UpdateState.Ready && State != UpdateState.Installing && Time.realtimeSinceStartup >= m_nextCheck)
			{
				CheckNow();
			}
			if (State == UpdateState.Ready)
			{
				UpdateReadyState();
			}
		}

		// ------------------------------------------------------------------
		// Checking and downloading
		// ------------------------------------------------------------------

		public void CheckNow()
		{
			if (State == UpdateState.Disabled || m_workerBusy || State == UpdateState.Installing)
			{
				return;
			}
			m_nextCheck = Time.realtimeSinceStartup + CheckInterval;
			State = UpdateState.Checking;
			StatusText = "Checking for updates...";
			string url = m_build.updateUrl + "latest.json";
			RunWorker(() => m_workerResultText = HttpGetString(url, 8000), OnManifestDownloaded);
		}

		private void OnManifestDownloaded()
		{
			if (m_workerError != null)
			{
				// The update server is often simply offline (e.g. the release PC is off): not an error for the player.
				State = UpdateState.Idle;
				StatusText = "Update server not reachable.";
				Debug.Log("[Updater] Check failed: " + m_workerError);
				return;
			}
			UpdateManifest manifest;
			try
			{
				manifest = JsonUtility.FromJson<UpdateManifest>(m_workerResultText);
			}
			catch (Exception ex)
			{
				Fail("Invalid update manifest: " + ex.Message);
				return;
			}
			if (manifest == null)
			{
				Fail("Invalid update manifest: empty");
				return;
			}
			if (!manifest.IsWellFormed(out string reason))
			{
				Fail("Invalid update manifest: " + reason);
				return;
			}
			if (manifest.build <= m_build.build)
			{
				State = UpdateState.UpToDate;
				StatusText = "Up to date (" + m_build.version + ").";
				return;
			}
			if (!manifest.HasValidSignature())
			{
				Fail("Update " + manifest.version + " is not signed with the release key - ignored.");
				return;
			}
			if (PlayerPrefs.GetInt(FailedBuildKey, 0) == manifest.build)
			{
				State = UpdateState.Failed;
				StatusText = "Update " + manifest.version + " failed to install before. See updates/update.log.";
				return;
			}
			if (!InstallFolderWritable(out string writeError))
			{
				Fail("Cannot update: the game folder is not writable (" + writeError + "). Move the game to a folder you own.");
				return;
			}
			Available = manifest;
			string zipPath = Path.Combine(m_updateDir, manifest.file);
			PendingUpdate pending = LoadPending();
			if (pending != null && pending.build == manifest.build && File.Exists(zipPath))
			{
				m_pending = pending;
				SetReady();
				return;
			}
			State = UpdateState.Downloading;
			DownloadProgress = 0f;
			StatusText = "Downloading update " + manifest.version + "...";
			string url = m_build.updateUrl + Uri.EscapeDataString(manifest.file);
			// A private temp file per process: several game windows may download the same update at once.
			string partPath = zipPath + "." + Process.GetCurrentProcess().Id + ".part";
			string expected = manifest.sha256.ToLowerInvariant();
			long size = manifest.size;
			RunWorker(() =>
			{
				// Another instance may already have finished the download.
				if (File.Exists(zipPath) && TryHashEquals(zipPath, expected))
				{
					return;
				}
				try
				{
					HttpDownloadFile(url, partPath, size);
					if (Sha256OfFile(partPath) != expected)
					{
						throw new Exception("checksum mismatch (download corrupted or tampered)");
					}
					try
					{
						if (File.Exists(zipPath))
						{
							File.Delete(zipPath);
						}
						File.Move(partPath, zipPath);
					}
					catch (IOException)
					{
						// Lost the race against another instance: fine if its file is complete.
						if (!TryHashEquals(zipPath, expected))
						{
							throw;
						}
					}
				}
				finally
				{
					TryDelete(partPath);
				}
			}, () => OnZipDownloaded(manifest, zipPath));
		}

		private static bool TryHashEquals(string path, string expected)
		{
			try
			{
				return Sha256OfFile(path) == expected;
			}
			catch
			{
				return false;
			}
		}

		private static void TryDelete(string path)
		{
			try
			{
				if (File.Exists(path))
				{
					File.Delete(path);
				}
			}
			catch
			{
			}
		}

		private void OnZipDownloaded(UpdateManifest manifest, string zipPath)
		{
			if (m_workerError != null)
			{
				Fail("Update download failed: " + m_workerError);
				// Transient problems (network, file busy) deserve another try soon.
				m_nextCheck = Time.realtimeSinceStartup + RetryInterval;
				return;
			}
			m_pending = new PendingUpdate
			{
				build = manifest.build,
				version = manifest.version,
				zipPath = zipPath,
				sha256 = manifest.sha256.ToLowerInvariant(),
				attempts = 0
			};
			SavePending(m_pending);
			SetReady();
		}

		private void SetReady()
		{
			State = UpdateState.Ready;
			m_countdownEnd = -1f;
			StatusText = "Update " + m_pending.version + " is ready.";
			Debug.Log("[Updater] " + StatusText);
		}

		// ------------------------------------------------------------------
		// Installing
		// ------------------------------------------------------------------

		/// <summary>
		/// Only restart where nothing is lost: the main menu or the version selection at startup
		/// (game state still undefined), and never during a multiplayer session.
		/// </summary>
		private static bool IsSafeToRestart()
		{
			try
			{
				GameManager gameManager = Singleton<GameManager>.Instance;
				GameManager.GameState state = gameManager != null ? gameManager.GetGameState() : GameManager.GameState.Undefined;
				if (state != GameManager.GameState.MainMenu && state != GameManager.GameState.Undefined)
				{
					return false;
				}
				BPRE.Multiplayer.MultiplayerSession session = BPRE.Multiplayer.MultiplayerSession.Instance;
				return session == null || session.State == BPRE.Multiplayer.MultiplayerSession.SessionState.Offline;
			}
			catch
			{
				return false;
			}
		}

		private void UpdateReadyState()
		{
			if (m_postponed || !IsSafeToRestart())
			{
				m_countdownEnd = -1f;
				return;
			}
			if (m_countdownEnd < 0f)
			{
				m_countdownEnd = Time.realtimeSinceStartup + RestartCountdown;
			}
			if (Time.realtimeSinceStartup >= m_countdownEnd)
			{
				InstallNow();
			}
		}

		private bool TryInstallPendingAtStartup()
		{
			PendingUpdate pending = LoadPending();
			if (pending == null)
			{
				return false;
			}
			if (pending.build <= m_build.build)
			{
				// Installed successfully last time: clean up.
				Debug.Log("[Updater] Update " + pending.version + " is installed.");
				DeletePending(pending);
				return false;
			}
			if (pending.attempts >= 2)
			{
				PlayerPrefs.SetInt(FailedBuildKey, pending.build);
				PlayerPrefs.Save();
				DeletePending(pending);
				Fail("Update " + pending.version + " could not be installed. See " + Path.Combine(m_updateDir, "update.log"));
				return true;
			}
			if (!File.Exists(pending.zipPath))
			{
				DeletePending(pending);
				return false;
			}
			int owner = ReadInstallLockOwner();
			if (owner != 0 && owner != Process.GetCurrentProcess().Id)
			{
				// Another window is installing right now; this window closes via FollowOtherInstaller.
				return false;
			}
			m_pending = pending;
			Available = null;
			InstallNow();
			return true;
		}

		// ------------------------------------------------------------------
		// Several game windows (e.g. local multiplayer): only one installs
		// ------------------------------------------------------------------

		private string InstallLockPath => Path.Combine(m_updateDir, InstallLockFileName);

		/// <summary>Process id holding a valid install lock, or 0 (no lock, stale lock, or owner gone).</summary>
		private int ReadInstallLockOwner()
		{
			try
			{
				FileInfo lockFile = new FileInfo(InstallLockPath);
				if (!lockFile.Exists || (DateTime.UtcNow - lockFile.LastWriteTimeUtc).TotalMinutes > InstallLockMaxAgeMinutes)
				{
					return 0;
				}
				if (!int.TryParse(File.ReadAllText(lockFile.FullName).Trim(), out int pid))
				{
					// Being written right now by another window.
					return -1;
				}
				return pid;
			}
			catch
			{
				// Unreadable: most likely held open by the window that is creating it.
				return File.Exists(InstallLockPath) ? -1 : 0;
			}
		}

		/// <summary>Atomically claims the install for this window. Stale locks (older than 15 minutes) are taken over.</summary>
		private bool TryAcquireInstallLock()
		{
			int self = Process.GetCurrentProcess().Id;
			for (int attempt = 0; attempt < 2; attempt++)
			{
				try
				{
					using (FileStream stream = new FileStream(InstallLockPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
					using (StreamWriter writer = new StreamWriter(stream))
					{
						writer.Write(self);
					}
					return true;
				}
				catch (IOException)
				{
					int owner = ReadInstallLockOwner();
					if (owner == self)
					{
						return true;
					}
					if (owner != 0)
					{
						return false;
					}
					TryDelete(InstallLockPath);
				}
			}
			return false;
		}

		/// <summary>
		/// When another window of this installation is installing an update, this window has to close
		/// too, otherwise the installer cannot replace the files. Close automatically where nothing is lost.
		/// </summary>
		private void FollowOtherInstaller()
		{
			int owner = ReadInstallLockOwner();
			bool other = owner != 0 && owner != Process.GetCurrentProcess().Id;
			if (other && !m_otherInstanceInstalling)
			{
				Debug.Log("[Updater] Another game window (pid " + owner + ") is installing an update.");
			}
			m_otherInstanceInstalling = other;
			if (!other)
			{
				m_quitForOtherInstallerAt = -1f;
				return;
			}
			if (IsSafeToRestart())
			{
				if (m_quitForOtherInstallerAt < 0f)
				{
					m_quitForOtherInstallerAt = Time.realtimeSinceStartup + 3f;
				}
				else if (Time.realtimeSinceStartup >= m_quitForOtherInstallerAt)
				{
					Debug.Log("[Updater] Closing so the update can be installed.");
					Application.Quit();
				}
			}
			else
			{
				m_quitForOtherInstallerAt = -1f;
			}
		}

		public void InstallNow()
		{
			if (m_pending == null || State == UpdateState.Installing)
			{
				return;
			}
			if (!TryAcquireInstallLock())
			{
				// Someone else is already on it; FollowOtherInstaller closes this window.
				m_otherInstanceInstalling = true;
				return;
			}
			State = UpdateState.Installing;
			StatusText = "Installing update " + m_pending.version + " - the game restarts in a moment...";
			try
			{
				m_pending.attempts++;
				SavePending(m_pending);
				string script = Path.Combine(m_updateDir, "apply_update.ps1");
				File.WriteAllText(script, ApplyScript);
				string exe = Process.GetCurrentProcess().MainModule.FileName;
				string target = Path.GetDirectoryName(Application.dataPath);
				string log = Path.Combine(m_updateDir, "update.log");
				ProcessStartInfo info = new ProcessStartInfo
				{
					FileName = "powershell.exe",
					Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File " + Quote(script) +
						" -WaitPid " + Process.GetCurrentProcess().Id +
						" -Zip " + Quote(m_pending.zipPath) +
						" -Sha256 " + m_pending.sha256 +
						" -Target " + Quote(target) +
						" -Exe " + Quote(exe) +
						" -Log " + Quote(log) +
						" -Lock " + Quote(InstallLockPath),
					UseShellExecute = false,
					CreateNoWindow = true
				};
				Process.Start(info);
				Debug.Log("[Updater] Installer started, quitting.");
				Application.Quit();
			}
			catch (Exception ex)
			{
				TryDelete(InstallLockPath);
				Fail("Could not start the installer: " + ex.Message);
			}
		}

		public void Postpone()
		{
			m_postponed = true;
			m_countdownEnd = -1f;
			StatusText = "Update " + (m_pending != null ? m_pending.version : string.Empty) + " will be installed at the next start.";
		}

		private static string Quote(string value)
		{
			return "\"" + value.Replace("\"", "") + "\"";
		}

		private const string ApplyScript = @"param([int]$WaitPid, [string]$Zip, [string]$Sha256, [string]$Target, [string]$Exe, [string]$Log, [string]$Lock)
$ErrorActionPreference = 'Stop'
function Write-Log([string]$m) { Add-Content -Path $Log -Value ((Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + ' ' + $m) }
try {
  Write-Log ""Update started: $Zip -> $Target""
  $deadline = (Get-Date).AddMinutes(2)
  while ((Get-Process -Id $WaitPid -ErrorAction SilentlyContinue) -and ((Get-Date) -lt $deadline)) { Start-Sleep -Milliseconds 300 }
  $exeFull = [IO.Path]::GetFullPath($Exe)
  $deadline = (Get-Date).AddMinutes(10)
  while ((Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exeFull }) -and ((Get-Date) -lt $deadline)) { Start-Sleep -Seconds 1 }
  $hash = (Get-FileHash -Algorithm SHA256 -Path $Zip).Hash.ToLowerInvariant()
  if ($hash -ne $Sha256.ToLowerInvariant()) { throw 'checksum mismatch' }
  $staging = Join-Path ([IO.Path]::GetTempPath()) ('bpre-update-' + [guid]::NewGuid().ToString('N'))
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  [IO.Compression.ZipFile]::ExtractToDirectory($Zip, $staging)
  $exeName = [IO.Path]::GetFileName($exeFull)
  $found = Get-ChildItem -Path $staging -Recurse -Filter $exeName | Select-Object -First 1
  if (-not $found) { throw ""$exeName not found in the update package"" }
  robocopy $found.DirectoryName $Target /E /IS /IT /R:10 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
  if ($LASTEXITCODE -ge 8) { throw ""copy failed (robocopy exit code $LASTEXITCODE)"" }
  Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item $Zip -Force -ErrorAction SilentlyContinue
  Write-Log 'Update installed'
} catch {
  Write-Log ('Update failed: ' + $_.Exception.Message)
} finally {
  if ($Lock) { Remove-Item $Lock -Force -ErrorAction SilentlyContinue }
}
Start-Process -FilePath $Exe -WorkingDirectory $Target
";

		// ------------------------------------------------------------------
		// Pending update bookkeeping
		// ------------------------------------------------------------------

		private PendingUpdate LoadPending()
		{
			try
			{
				string path = Path.Combine(m_updateDir, PendingFileName);
				return File.Exists(path) ? JsonUtility.FromJson<PendingUpdate>(File.ReadAllText(path)) : null;
			}
			catch
			{
				return null;
			}
		}

		private void SavePending(PendingUpdate pending)
		{
			File.WriteAllText(Path.Combine(m_updateDir, PendingFileName), JsonUtility.ToJson(pending));
		}

		private void DeletePending(PendingUpdate pending)
		{
			try
			{
				File.Delete(Path.Combine(m_updateDir, PendingFileName));
				if (pending != null && !string.IsNullOrEmpty(pending.zipPath) && File.Exists(pending.zipPath))
				{
					File.Delete(pending.zipPath);
				}
			}
			catch
			{
			}
		}

		private static bool InstallFolderWritable(out string error)
		{
			error = null;
			try
			{
				string probe = Path.Combine(Path.GetDirectoryName(Application.dataPath), ".bpre_write_test");
				File.WriteAllText(probe, "ok");
				File.Delete(probe);
				return true;
			}
			catch (Exception ex)
			{
				error = ex.Message;
				return false;
			}
		}

		private void Fail(string message)
		{
			State = UpdateState.Failed;
			StatusText = message;
			Debug.LogWarning("[Updater] " + message);
		}

		// ------------------------------------------------------------------
		// Background work (HTTP and hashing must not block the game)
		// ------------------------------------------------------------------

		private void RunWorker(Action work, Action onDone)
		{
			m_workerBusy = true;
			m_workerError = null;
			m_onWorkerDone = onDone;
			Thread thread = new Thread(() =>
			{
				try
				{
					work();
				}
				catch (Exception ex)
				{
					m_workerError = ex.Message;
				}
				m_workerDone = true;
			})
			{
				IsBackground = true,
				Name = "BPRE.Updater"
			};
			thread.Start();
		}

		private static string HttpGetString(string url, int timeoutMs)
		{
			HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url + (url.Contains("?") ? "&" : "?") + "t=" + DateTime.UtcNow.Ticks);
			request.Timeout = timeoutMs;
			request.ReadWriteTimeout = timeoutMs;
			request.UserAgent = "BPRE-Updater";
			using (WebResponse response = request.GetResponse())
			using (StreamReader reader = new StreamReader(response.GetResponseStream()))
			{
				return reader.ReadToEnd();
			}
		}

		private void HttpDownloadFile(string url, string path, long expectedSize)
		{
			HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
			request.Timeout = 15000;
			request.ReadWriteTimeout = 30000;
			request.UserAgent = "BPRE-Updater";
			using (WebResponse response = request.GetResponse())
			using (Stream input = response.GetResponseStream())
			using (FileStream output = new FileStream(path, FileMode.Create, FileAccess.Write))
			{
				byte[] buffer = new byte[81920];
				long total = 0;
				int read;
				while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
				{
					output.Write(buffer, 0, read);
					total += read;
					if (total > expectedSize + 1024)
					{
						throw new Exception("download larger than announced");
					}
					DownloadProgress = expectedSize > 0 ? (float)total / expectedSize : 0f;
				}
				if (total != expectedSize)
				{
					throw new Exception("incomplete download (" + total + " of " + expectedSize + " bytes)");
				}
			}
		}

		private static string Sha256OfFile(string path)
		{
			using (FileStream stream = File.OpenRead(path))
			using (SHA256 sha = SHA256.Create())
			{
				byte[] hash = sha.ComputeHash(stream);
				char[] chars = new char[hash.Length * 2];
				const string hex = "0123456789abcdef";
				for (int i = 0; i < hash.Length; i++)
				{
					chars[i * 2] = hex[hash[i] >> 4];
					chars[i * 2 + 1] = hex[hash[i] & 0xf];
				}
				return new string(chars);
			}
		}

		// ------------------------------------------------------------------
		// GUI
		// ------------------------------------------------------------------

		private void OnGUI()
		{
			bool inMenu = false;
			try
			{
				GameManager gameManager = Singleton<GameManager>.Instance;
				inMenu = gameManager != null && gameManager.GetGameState() == GameManager.GameState.MainMenu;
			}
			catch
			{
			}
			float scale = Mathf.Max(1f, Screen.height / 720f);
			if (m_bannerStyle == null)
			{
				m_bannerStyle = new GUIStyle(GUI.skin.box) { fontSize = Mathf.RoundToInt(15f * scale), wordWrap = true, alignment = TextAnchor.MiddleLeft };
				m_bannerStyle.normal.textColor = Color.white;
				m_smallStyle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(11f * scale), alignment = TextAnchor.LowerRight };
				m_smallStyle.normal.textColor = new Color(1f, 1f, 1f, 0.7f);
				m_buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = Mathf.RoundToInt(14f * scale) };
			}
			if (inMenu)
			{
				GUI.Label(new Rect(0f, Screen.height - 24f * scale, Screen.width - 8f * scale, 20f * scale), "Build " + m_build.version, m_smallStyle);
			}
			string text = null;
			bool showButtons = false;
			if (m_otherInstanceInstalling)
			{
				text = IsSafeToRestart()
					? "Another game window is installing an update. This window closes in a moment."
					: "Another game window is installing an update. Close this window when you are done to finish it.";
			}
			else
			switch (State)
			{
			case UpdateState.Downloading:
				if (inMenu)
				{
					text = "Downloading update " + (Available != null ? Available.version : string.Empty) + "  " + Mathf.RoundToInt(DownloadProgress * 100f) + "%";
				}
				break;
			case UpdateState.Ready:
				if (m_postponed)
				{
					break;
				}
				if (IsSafeToRestart() && m_countdownEnd > 0f)
				{
					int seconds = Mathf.CeilToInt(Mathf.Max(0f, m_countdownEnd - Time.realtimeSinceStartup));
					text = "Update " + m_pending.version + " is ready. The game restarts to install it in " + seconds + " s.";
					showButtons = true;
				}
				else if (inMenu)
				{
					text = "Update " + m_pending.version + " is ready and installs when you leave the multiplayer session.";
				}
				break;
			case UpdateState.Installing:
			case UpdateState.Failed:
				if (inMenu || State == UpdateState.Installing)
				{
					text = StatusText;
				}
				break;
			}
			if (text == null)
			{
				return;
			}
			float width = Mathf.Min(Screen.width - 32f, 640f * scale);
			float height = (showButtons ? 84f : 44f) * scale;
			Rect rect = new Rect((Screen.width - width) * 0.5f, Screen.height - height - 40f * scale, width, height);
			GUI.Box(rect, GUIContent.none, m_bannerStyle);
			GUI.Label(new Rect(rect.x + 12f * scale, rect.y + 6f * scale, rect.width - 24f * scale, 32f * scale), text, m_bannerStyle);
			if (showButtons)
			{
				float buttonWidth = 160f * scale;
				float y = rect.y + 44f * scale;
				if (GUI.Button(new Rect(rect.xMax - 2f * buttonWidth - 20f * scale, y, buttonWidth, 30f * scale), "Restart now", m_buttonStyle))
				{
					InstallNow();
				}
				if (GUI.Button(new Rect(rect.xMax - buttonWidth - 12f * scale, y, buttonWidth, 30f * scale), "Later", m_buttonStyle))
				{
					Postpone();
				}
			}
		}
	}
}
