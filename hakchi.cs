﻿using com.clusterrr.clovershell;
using com.clusterrr.hakchi_gui.Properties;
using com.clusterrr.hakchi_gui.Tasks;
using com.clusterrr.ssh;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace com.clusterrr.hakchi_gui
{
    public static class hakchi
    {
        public const string AVAHI_SERVICE_NAME = "_hakchi._tcp.local.";
        public const string STATIC_IP = "10.234.137.10";
        public const string USERNAME = "root";
        public const string PASSWORD = "";
        public const long BLOCK_SIZE = 4096;

        public static ISystemShell Shell { get; private set; }
        public static bool Connected { get; private set; }
        public static event OnConnectedEventHandler OnConnected = delegate { };
        public static event OnDisconnectedEventHandler OnDisconnected = delegate { };

        public static MainForm.ConsoleType? DetectedConsoleType { get; private set; }
        public static bool CustomFirmwareLoaded { get; private set; }
        public static string BootVersion { get; private set; }
        public static string KernelVersion { get; private set; }
        public static string ScriptVersion { get; private set; }
        public static string UniqueID { get; private set; }
        public static bool CanInteract { get; private set; }
        public static bool MinimalMemboot { get; private set; }

        public static string ConfigPath { get; private set; }
        public static string RemoteGameSyncPath { get; private set; }
        public static string SystemCode { get; private set; }
        public static string MediaPath { get; private set; }
        public static string GamesPath { get; private set; }
        public static string RootFsPath { get; private set; }
        public static string GamesProfilePath { get; private set; }
        public static string SquashFsPath { get; private set; }
        public static string GamesSquashFsPath
        {
            get
            {
                switch (ConfigIni.Instance.ConsoleType)
                {
                    default:
                    case MainForm.ConsoleType.NES:
                    case MainForm.ConsoleType.Famicom:
                        return "/usr/share/games/nes/kachikachi";
                    case MainForm.ConsoleType.SNES:
                    case MainForm.ConsoleType.SuperFamicom:
                        return "/usr/share/games";
                }
            }
        }

        public static string GetRemoteGameSyncPath(MainForm.ConsoleType consoleType, string region = null)
        {
            if (RemoteGameSyncPath == null)
                throw new NullReferenceException("No valid sync path is available");

            if (ConfigIni.Instance.SeparateGameStorage)
            {
                string systemCode = string.Empty;
                switch (consoleType)
                {
                    default:
                        throw new ArgumentException("No valid console type was given");
                    case MainForm.ConsoleType.NES:
                        systemCode = "nes-usa";
                        break;
                    case MainForm.ConsoleType.Famicom:
                        systemCode = "nes-jpn";
                        break;
                    case MainForm.ConsoleType.SNES:
                        switch (region.ToLower())
                        {
                            case "eur":
                                systemCode = "snes-eur";
                                break;
                            case "usa":
                                systemCode = "snes-usa";
                                break;
                            default:
                                throw new ArgumentNullException("Region code is null and selected consoleType is SNES");
                        }
                        break;
                    case MainForm.ConsoleType.SuperFamicom:
                        systemCode = "snes-jpn";
                        break;
                }
                return $"{RemoteGameSyncPath}/{systemCode}";
            }
            return RemoteGameSyncPath;
        }

        public static string GetRemoteGameSyncPath()
        {
            if (RemoteGameSyncPath == null)
                throw new NullReferenceException("No valid sync path is available");
            if (ConfigIni.Instance.SeparateGameStorage && SystemCode != null)
            {
                return $"{RemoteGameSyncPath}/{SystemCode}";
            }
            return RemoteGameSyncPath;
        }

        public static string MinimumHakchiBootVersion
        {
            get { return "1.0.1"; }
        }

        public static string MinimumHakchiKernelVersion
        {
            get { return "3.4.112"; }
        }

        public static string MinimumHakchiScriptVersion
        {
            get { return "1.0.3"; }
        }

        public static string MinimumHakchiScriptRevision
        {
            get { return "110"; }
        }

        public static string CurrentHakchiScriptVersion
        {
            get { return "1.0.3"; }
        }

        public static string CurrentHakchiScriptRevision
        {
            get { return "110"; }
        }

        static hakchi()
        {
            Shell = null;
            clearProperties();
        }

        private static void clearProperties()
        {
            Connected = false;
            DetectedConsoleType = null;
            CustomFirmwareLoaded = false;
            BootVersion = "";
            KernelVersion = "";
            ScriptVersion = "";
            CanInteract = false;
            MinimalMemboot = false;
            UniqueID = null;
            ConfigPath = "/etc/preinit.d/p0000_config";
            RemoteGameSyncPath = "/var/lib/hakchi/games";
            SystemCode = null;
            MediaPath = "/media";
            GamesPath = "/var/games";
            RootFsPath = "/var/lib/hakchi/rootfs";
            GamesProfilePath = "/var/saves";
            SquashFsPath = "/var/squashfs";
        }

        private static List<ISystemShell> shells = new List<ISystemShell>();

        public static void Initialize()
        {
            if (shells.Any())
                return;

            // placeholder shell
            shells.Add(new UnknownShell());
            Shell = shells.First();

            // clovershell (for legacy compatibility)
            var clovershell = new ClovershellConnection() { AutoReconnect = true };
            clovershell.OnConnected += Shell_OnConnected;
            clovershell.OnDisconnected += Shell_OnDisconnected;
            shells.Add(clovershell);

            // new high-tech but slow SSH connection
            var ssh = new SshClientWrapper(AVAHI_SERVICE_NAME, STATIC_IP, 22, USERNAME, PASSWORD) { AutoReconnect = true };
            ssh.OnConnected += Shell_OnConnected;
            ssh.OnDisconnected += Shell_OnDisconnected;
            shells.Add(ssh);

            // start their watchers
            clovershell.Enabled = true;
            ssh.Enabled = true;
        }

        public static void Shutdown()
        {
            shells.ForEach(shell => shell.Dispose());
            shells.Clear();
            Shell = null;
        }

        public static void Shell_OnDisconnected()
        {
            // clear up used shell and reenable all
            Shell = shells.First();
            shells.ForEach(shell => shell.Enabled = true);

            clearProperties();
            OnDisconnected();
        }

        public static void Shell_OnConnected(ISystemShell caller)
        {
            // set calling shell as current used shell and disable others
            Shell = caller;
            shells.ForEach(shell => { if (shell != caller) shell.Enabled = false; });
            try
            {
                Connected = Shell.IsOnline;
                if (!Shell.IsOnline)
                {
                    throw new IOException("Shell connection should be online!");
                }

                MinimalMemboot = Shell.Execute("source /hakchi/config; [ \"$cf_memboot\" = \"y\" ]") == 0;

                // detect unique id
                UniqueID = Shell.ExecuteSimple("echo \"`devmem 0x01C23800``devmem 0x01C23804``devmem 0x01C23808``devmem 0x01C2380C`\"").Trim().Replace("0x", "");
                Debug.WriteLine($"Detected device unique ID: {UniqueID}");

                if (MinimalMemboot)
                {
                    OnConnected(caller); // still gotta call this
                    return;
                }

                // detect running/mounted firmware
                string board = Shell.ExecuteSimple("cat /etc/clover/boardtype", 3000, true);
                string region = Shell.ExecuteSimple("cat /etc/clover/REGION", 3000, true);
                DetectedConsoleType = translateConsoleType(board, region);
                if (DetectedConsoleType == MainForm.ConsoleType.Unknown)
                {
                    throw new IOException("Unable to determine mounted firmware");
                }
                var customFirmwareLoaded = Shell.ExecuteSimple("hakchi currentFirmware");
                CustomFirmwareLoaded = customFirmwareLoaded != "_nand_";
                Debug.WriteLine(string.Format("Detected mounted board: {0}", board));
                Debug.WriteLine(string.Format("Detected mounted region: {0}", region));
                Debug.WriteLine(string.Format("Detected mounted firmware: {0}", customFirmwareLoaded));

                // detect running versions
                var versions = Shell.ExecuteSimple("source /var/version && echo \"$bootVersion $kernelVersion $hakchiVersion\"", 500, true).Split(' ');
                BootVersion = versions[0];
                KernelVersion = versions[1];
                ScriptVersion = versions[2];
                CanInteract = !SystemRequiresReflash() && !SystemRequiresRootfsUpdate();

                // only do more interaction if safe to do so
                if (!CanInteract) return;

                // detect basic paths
                RemoteGameSyncPath = Shell.ExecuteSimple("hakchi findGameSyncStorage", 2000, true).Trim();
                SystemCode = Shell.ExecuteSimple("hakchi eval 'echo \"$sftype-$sfregion\"'", 2000, true).Trim();
                GamesPath = Shell.ExecuteSimple("hakchi get gamepath", 2000, true).Trim();
                RootFsPath = Shell.ExecuteSimple("hakchi get rootfs", 2000, true).Trim();
                SquashFsPath = Shell.ExecuteSimple("hakchi get squashfs", 2000, true).Trim();

                // load config
                ConfigIni.SetConfigDictionary(LoadConfig());

                // chain to other OnConnected events
                OnConnected(caller);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
                CanInteract = false;
                MinimalMemboot = false;
            }
        }

        private static MainForm.ConsoleType translateConsoleType(string board, string region)
        {
            switch (board)
            {
                default:
                case "dp-nes":
                case "dp-hvc":
                    switch (region)
                    {
                        case "EUR_USA":
                            return MainForm.ConsoleType.NES;
                        case "JPN":
                            return MainForm.ConsoleType.Famicom;
                    }
                    break;
                case "dp-shvc":
                    switch (region)
                    {
                        case "USA":
                        case "EUR":
                            return MainForm.ConsoleType.SNES;
                        case "JPN":
                            return MainForm.ConsoleType.SuperFamicom;
                    }
                    break;
            }
            return MainForm.ConsoleType.Unknown;
        }

        public static bool SystemRequiresReflash()
        {
            bool requiresReflash = false;
            try
            {
                string kernelVersion = KernelVersion.Substring(0, KernelVersion.LastIndexOf('.'));
                if (!Shared.IsVersionGreaterOrEqual(kernelVersion, hakchi.MinimumHakchiKernelVersion) ||
                    !Shared.IsVersionGreaterOrEqual(BootVersion, hakchi.MinimumHakchiBootVersion))
                {
                    requiresReflash = true;
                }
            }
            catch
            {
                requiresReflash = true;
            }
            return requiresReflash;
        }

        public static bool SystemRequiresRootfsUpdate()
        {
            bool requiresUpdate = false;
            try
            {
                string scriptVersion = ScriptVersion.Substring(ScriptVersion.IndexOf('v') + 1);
                scriptVersion = scriptVersion.Substring(0, scriptVersion.LastIndexOf('('));

                var scriptElems = scriptVersion.Split(new char[] { '-' });
                if (!Shared.IsVersionGreaterOrEqual(scriptElems[0], hakchi.MinimumHakchiScriptVersion) ||
                    !(int.Parse(scriptElems[1]) >= int.Parse(hakchi.MinimumHakchiScriptRevision)))
                {
                    requiresUpdate = true;
                }
            }
            catch
            {
                requiresUpdate = true;
            }
            return requiresUpdate;
        }

        public static bool SystemEligibleForRootfsUpdate()
        {
            bool eligibleForUpdate = false;
            try
            {
                string scriptVersion = ScriptVersion.Substring(ScriptVersion.IndexOf('v') + 1);
                scriptVersion = scriptVersion.Substring(0, scriptVersion.LastIndexOf('('));

                var scriptElems = scriptVersion.Split(new char[] { '-' });
                if (!Shared.IsVersionGreaterOrEqual(scriptElems[0], hakchi.CurrentHakchiScriptVersion) ||
                    !(int.Parse(scriptElems[1]) >= int.Parse(hakchi.CurrentHakchiScriptRevision)))
                {
                    eligibleForUpdate = true;
                }
            }
            catch
            {
                eligibleForUpdate = true;
            }
            return eligibleForUpdate;
        }

        public static Tasker.Conclusion ShowSplashScreen(Tasker tasker, Object syncObject)
        {
            return ShowSplashScreen() == 0 ? Tasker.Conclusion.Success : Tasker.Conclusion.Error;
        }

        public static int ShowSplashScreen()
        {
            var splashScreenStream = new MemoryStream(Resources.splash);
            Shell.ExecuteSimple("uistop");

            return Shell.Execute("gunzip -c - > /dev/fb0", splashScreenStream, null, null, 3000);
        }

        public static void SyncConfig(Dictionary<string, string> config, bool reboot = false)
        {
            using (var stream = new MemoryStream())
            {
                if (config != null && config.Count > 0)
                {
                    Debug.WriteLine("Saving p00000_config values");
                    foreach (var key in config.Keys)
                    {
                        var data = Encoding.UTF8.GetBytes(string.Format("cfg_{0}='{1}'\n", key, config[key].Replace(@"'", @"\'")));
                        stream.Write(data, 0, data.Length);
                    }
                }
                Shell.Execute($"hakchi eval", stream, null, null, 3000, true);
            }
            if (reboot)
            {
                try
                {
                    Shell.ExecuteSimple("reboot", 100);
                }
                catch { }
            }
        }

        public static Dictionary<string, string> LoadConfig()
        {
            var config = new Dictionary<string, string>();

            try
            {
                Debug.WriteLine("Reading p0000_config file");
                string configFile;
                using (var stream = new MemoryStream())
                {
                    Shell.Execute($"cat {ConfigPath}", null, stream, null, 2000, true);
                    configFile = Encoding.UTF8.GetString(stream.ToArray());
                }

                if (!string.IsNullOrEmpty(configFile))
                {
                    MatchCollection collection = Regex.Matches(configFile, @"cfg_([^=]+)=(""(?:[^""\\]*(?:\\.[^""\\]*)*)""|\'(?:[^\'\\]*(?:\\.[^\'\\]*)*)\')", RegexOptions.Multiline | RegexOptions.IgnoreCase);
                    foreach (Match match in collection)
                    {
                        string param = match.Groups[1].Value;
                        string value = match.Groups[2].Value;
                        value = value.Substring(1, value.Length - 2).Replace("\'", "'").Replace("\\\"", "\"");
                        config[param] = value;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error reading p0000_config file : " + ex.Message + ex.StackTrace);
                config.Clear();
            }
            return config;
        }

        public static bool GetStorageStats(out long gamesSize, out long saveStatesSize, out long storageTotal, out long storageUsed, out long storageFree)
        {
            try
            {
                var storage = Shell.ExecuteSimple("df \"$(hakchi findGameSyncStorage)\" | tail -n 1 | awk '{ print $2 \" | \" $3 \" | \" $4 }'", 2000, true).Split('|');
                bool externalSaveStates = Shell.ExecuteSimple("mount | grep /var/lib/clover").Trim().Length > 0;
                gamesSize = long.Parse(Shell.ExecuteSimple("du -s \"$(hakchi findGameSyncStorage)\" | awk '{ print $1 }'", 2000, true)) * 1024;
                saveStatesSize = long.Parse(Shell.ExecuteSimple("du -s \"$(readlink /var/saves)\" | awk '{ print $1 }'", 2000, true)) * 1024;
                storageTotal = long.Parse(storage[0]) * 1024;
                storageUsed = long.Parse(storage[1]) * 1024;
                storageFree = long.Parse(storage[2]) * 1024;
                Debug.WriteLine(string.Format("Storage size: {0:F1}MB, used: {1:F1}MB, free: {2:F1}MB", storageTotal / 1024.0 / 1024.0, storageUsed / 1024.0 / 1024.0, storageFree / 1024.0 / 1024.0));
                Debug.WriteLine(string.Format("Used by games: {0:F1}MB", gamesSize / 1024.0 / 1024.0));
                Debug.WriteLine(string.Format("Used by save-states: {0:F1}MB", saveStatesSize / 1024.0 / 1024.0));
                Debug.WriteLine(string.Format("Used by other files (mods, configs, etc.): {0:F1}MB", (storageUsed - gamesSize - saveStatesSize) / 1024.0 / 1024.0));
                Debug.WriteLine(string.Format("Available for games: {0:F1}MB", (storageFree + gamesSize) / 1024.0 / 1024.0));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error: " + ex.Message + ex.StackTrace);
                gamesSize = -1;
                saveStatesSize = -1;
                storageTotal = -1;
                storageUsed = -1;
                storageFree = -1;
                return false;
            }
            return true;
        }

        public static Image TakeScreenshot()
        {
            var screenshot = new Bitmap(1280, 720, PixelFormat.Format24bppRgb);
            var rawStream = new MemoryStream();
            Shell.ExecuteSimple("hakchi uipause");
            Shell.Execute("cat /dev/fb0", null, rawStream, null, 2000, true);
            Shell.ExecuteSimple("hakchi uiresume");
            var raw = rawStream.ToArray();
            BitmapData data = screenshot.LockBits(
                new Rectangle(0, 0, screenshot.Width, screenshot.Height),
                ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);

            int rawOffset = 0;
            unsafe
            {
                for (int y = 0; y < screenshot.Height; ++y)
                {
                    byte* row = (byte*)data.Scan0 + (y * data.Stride);
                    int columnOffset = 0;
                    for (int x = 0; x < screenshot.Width; ++x)
                    {
                        row[columnOffset] = raw[rawOffset];
                        row[columnOffset + 1] = raw[rawOffset + 1];
                        row[columnOffset + 2] = raw[rawOffset + 2];

                        columnOffset += 3;
                        rawOffset += 4;
                    }
                }
            }
            screenshot.UnlockBits(data);
            return screenshot;
        }

    }
}
