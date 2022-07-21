﻿using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows.Forms;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModLib.Util;

namespace HATE
{

    public partial class MainForm : Form
    {
        private static class Style
        {
            private static readonly Color _btnRestoreColor = Color.LimeGreen;
            private static readonly Color _btnCorruptColor = Color.Coral;
            private static readonly string _btnRestoreLabel = " -RESTORE- ";
            private static readonly string _btnCorruptLabel = " -CORRUPT- ";
            private static readonly Color _optionSet = Color.Yellow;
            private static readonly Color _optionUnset = Color.White;

            public static Color GetOptionColor(bool b) { return b ? _optionSet : _optionUnset; }
            public static Color GetCorruptColor(bool b) { return b ? _btnCorruptColor : _btnRestoreColor; }
            public static string GetCorruptLabel(bool b) { return b ? _btnCorruptLabel : _btnRestoreLabel; }
        }

        private StreamWriter _logWriter;

        private bool _shuffleGFX = false;
        private bool _shuffleText = false;
        private bool _hitboxFix = false;
        private bool _shuffleFont = false;
        private bool _shuffleBG = false;
        private bool _shuffleAudio = false;
        private bool _corrupt = false;
        private bool _friskMode = false;
        private float _truePower = 0;
        private readonly string _defaultPowerText = "0 - 255";
        private readonly string _dataWin = "data.win";

        private static readonly DateTime _unixTimeZero = new DateTime(1970, 1, 1);
        private Random _random;

        private WindowsPrincipal windowsPrincipal;

        private UndertaleData Data;

        public MainForm()
        {
            InitializeComponent();

            MsgBoxHelpers.mainForm = this;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                windowsPrincipal = new WindowsPrincipal(WindowsIdentity.GetCurrent());

                //This is so it doesn't keep starting the program over and over in case something messes up
                if (Process.GetProcessesByName("HATE").Length == 1)
                {
                    if (Directory.GetCurrentDirectory().Contains(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)) || Directory.GetCurrentDirectory().Contains(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)) && !IsElevated)
                    {
                        string message = $"The game is in a system protected folder and we need elevated permissions in order to mess with {_dataWin}.\nDo you allow us to get elevated permissions (if you press no this will just close the program as we can't do anything)";
                        if (MsgBoxHelpers.ShowMessage(message, MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                        {
                            // Restart program and run as admin
                            var exeName = Process.GetCurrentProcess().MainModule.FileName;
                            ProcessStartInfo startInfo = new ProcessStartInfo(exeName);
                            startInfo.Arguments = "true";
                            startInfo.Verb = "runas";
                            Process.Start(startInfo);
                            Close();
                            return;
                        }
                        else
                        {
                            Close();
                            return;
                        }
                    }
                }
            }

            _random = new Random();

            EnableControls(false);

            if (!File.Exists(_dataWin))
            {
                if (File.Exists("game.ios"))
                    _dataWin = "game.ios";
                else if (File.Exists("assets/game.unx"))
                    _dataWin = "assets/game.unx";
                else if (File.Exists("game.unx"))
                    _dataWin = "game.unx";
                else
                {
                    MsgBoxHelpers.ShowMessage("We couldn't find any game in this folder, check that this is in the right folder.");
                    Close();
                    return;
                }
            }

            _dataWin = Path.GetFullPath(_dataWin);
        }
        private async void MainForm_Load(object sender, EventArgs e)
        {
            EnableControls(false);
            await LoadFile(_dataWin);
            AfterDataLoad();
            if (Data is not null)
            {
                var displayName = Data.GeneralInfo.DisplayName.Content.ToLower();
                if (!(displayName.StartsWith("undertale") || displayName.StartsWith("deltarune")))
                    this.Invoke(delegate
                    {
                        MsgBoxHelpers.ShowWarning("HATE-UML's support of this game may be limited. Unless this is a mod of a Toby Fox game, DO NOT report any problems you might experience.", "Not a Toby Fox game");
                    }
            }
        }
        private void AfterDataLoad()
        {
            this.Invoke(delegate
            {      
                if (Data is not null)
                {
                    EnableControls(true);
                    lblGameName.Text = Data.GeneralInfo.DisplayName.Content;
                }
                UpdateCorrupt();
            });
        }

        private void DisposeGameData()
        {
            if (Data is not null)
            {
                Data.Dispose();
                Data = null;

                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect();
            }
        }

        private async Task LoadFile(string filename)
        {
            DisposeGameData();
            Task t = Task.Run(() =>
            {
                UndertaleData data = null;
                try
                {
                    using (var stream = new FileStream(filename, FileMode.Open, FileAccess.Read))
                    {
                        data = UndertaleIO.Read(stream, warning =>
                        {
                            MsgBoxHelpers.ShowWarning(warning, "Loading warning");
                        }, message =>
                        {
                            Invoke(delegate
                            {
                                lblGameName.Text = message;
                            });
                        });
                    }

                    UndertaleEmbeddedTexture.TexData.ClearSharedStream();
                }
                catch (Exception e)
                {
                    MsgBoxHelpers.ShowError("An error occured while trying to load:\n" + e.Message, "Load error");
                }

                if (data != null)
                {
                    if (data.UnsupportedBytecodeVersion)
                    {
                        MsgBoxHelpers.ShowWarning("Only bytecode versions 13 to 17 are supported for now, you are trying to load " + data.GeneralInfo.BytecodeVersion + ". A lot of code is disabled and something will likely break.", "Unsupported bytecode version");
                    }

                    Data = data;

                    Data.ToolInfo.ProfileMode = false;
                    Data.ToolInfo.AppDataProfiles = "";
                }
            });
            await t;

            // Clear "GC holes" left in the memory in process of data unserializing
            // https://docs.microsoft.com/en-us/dotnet/api/system.runtime.gcsettings.largeobjectheapcompactionmode?view=net-6.0
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect();
        }
        private async Task SaveFile(string filename)
        {
            if (Data == null)
                return;

            Task t = Task.Run(() =>
            {
                bool SaveSucceeded = true;

                try
                {
                    using (var stream = new FileStream(filename + "temp", FileMode.Create, FileAccess.Write))
                    {
                        UndertaleIO.Write(stream, Data, message =>
                        {
                            Invoke(delegate
                            {
                                lblGameName.Text = message;
                            });
                        });
                    }

                    UndertaleEmbeddedTexture.TexData.ClearSharedStream();
                    if (Data.UseQoiFormat)
                        QoiConverter.ClearSharedBuffer();
                }
                catch (Exception e)
                {
                    if (!UndertaleIO.IsDictionaryCleared)
                    {
                        try
                        {
                            var listChunks = Data.FORM.Chunks.Values.Select(x => x as IUndertaleListChunk);
                            Parallel.ForEach(listChunks.Where(x => x is not null), (chunk) =>
                            {
                                chunk.ClearIndexDict();
                            });

                            UndertaleIO.IsDictionaryCleared = true;
                        }
                        catch { }
                    }

                    MsgBoxHelpers.ShowError("An error occured while trying to save:\n" + e.Message, "Save error");

                    SaveSucceeded = false;
                }
                // Don't make any changes unless the save succeeds.
                try
                {
                    if (SaveSucceeded)
                    {
                        // It saved successfully!
                        // If we're overwriting a previously existing data file, we're going to delete it now.
                        // Then, we're renaming it back to the proper (non-temp) file name.
                        if (File.Exists(filename))
                            File.Delete(filename);
                        File.Move(filename + "temp", filename);
                    }
                    else
                    {
                        // It failed, but since we made a temp file for saving, no data was overwritten or destroyed (hopefully)
                        // We need to delete the temp file though (if it exists).
                        if (File.Exists(filename + "temp"))
                            File.Delete(filename + "temp");
                    }
                }
                catch (Exception exc)
                {
                    MsgBoxHelpers.ShowError("An error occured while trying to save:\n" + exc.Message, "Save error");

                    SaveSucceeded = false;
                }

                return Task.CompletedTask;
            });
            await t;

            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect();
        }

        public bool IsElevated
        {
            get
            {
                return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ?
                    windowsPrincipal.IsInRole(WindowsBuiltInRole.Administrator)
                    : false;
            }
        }

        public string WindowsExecPrefix
        {
            get => RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                    || RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                    || RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD) /* unix platforms */
                    ? "wine " : "";
        }
        public string GetGame()
        {
            if (File.Exists("runner")) { return "runner"; }
            else if (File.Exists(Data.GeneralInfo.FileName + ".exe")) { return $"{Data.GeneralInfo.FileName}.exe"; }
            else
            {
                var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                foreach (string file in Directory.EnumerateFiles(dir))
                {
                    if (Path.GetFileNameWithoutExtension(file) != "HATE" && file.EndsWith(".exe"))
                    {
                        var executable = Path.GetFileName(file);
                        return executable;
                    }
                }
                if (dir.IndexOf(".app") >= 0)
                {
                    return dir.Substring(0, dir.IndexOf(".app") + ".app".Length);
                }
            }
            return "";
        }

        private void btnLaunch_Clicked(object sender, EventArgs e)
        {
            EnableControls(false);

            string game = GetGame();
            if (game != "")
            {
                bool wine = game.EndsWith(".exe") && WindowsExecPrefix.Length > 0;
                ProcessStartInfo processStartInfo = new ProcessStartInfo(wine ? WindowsExecPrefix : game)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    WorkingDirectory = Path.GetDirectoryName(game)
                };
                if (wine)
                    processStartInfo.ArgumentList.Add(game);
                var process = Process.Start(processStartInfo);
                if (process is null || process.HasExited)
                    MsgBoxHelpers.ShowError("Failed to start the game.");
            }
            else
                MsgBoxHelpers.ShowError("I don't know how to run this game.\nPlease run the game manually.");

            EnableControls(true);
        }

        private async void button_Corrupt_Clicked(object sender, EventArgs e)
        {
            EnableControls(false);

            try { _logWriter = new StreamWriter("HATE.log", true); }
            catch (Exception) { MsgBoxHelpers.ShowError("Could not set up the log file."); }

            bool successful = false;

            if (!await Setup()) goto End;
            if (_shuffleGFX && !Functionality.ShuffleGFX_Func(Data, _random, _truePower, _logWriter, _friskMode)) goto End;
            if (_hitboxFix && !Functionality.HitboxFix_Func(Data, _random, _truePower, _logWriter, _friskMode)) goto End;
            if (_shuffleText && !Functionality.ShuffleText_Func(Data, _random, _truePower, _logWriter, _friskMode)) goto End;
            if (_shuffleFont && !Functionality.ShuffleFont_Func(Data, _random, _truePower, _logWriter, _friskMode)) goto End;
            if (_shuffleBG && !Functionality.ShuffleBG_Func(Data, _random, _truePower, _logWriter, _friskMode)) goto End;
            if (_shuffleAudio && !Functionality.ShuffleAudio_Func(Data, _random, _truePower, _logWriter, _friskMode)) goto End;
            successful = true;

        End:
            _logWriter.Close();
            if (successful)
            {
                await SaveFile(_dataWin);
                AfterDataLoad();
            }
            EnableControls(true);
        }

        public void EnableControls(bool state)
        {
            btnCorrupt.Enabled = state;
            btnLaunch.Enabled = state;
            chbShuffleText.Enabled = state;
            chbShuffleGFX.Enabled = state;
            chbHitboxFix.Enabled = state;
            chbShuffleFont.Enabled = state;
            chbShuffleBG.Enabled = state;
            chbShuffleAudio.Enabled = state;
            label1.Enabled = state;
            label2.Enabled = state;
            txtPower.Enabled = state;
            txtSeed.Enabled = state;
        }

        public async Task<bool> Setup()
        {
            _logWriter.WriteLine("-------------- Session at: " + DateTime.Now.ToLongDateString() + " " + DateTime.Now.ToLongTimeString() + "\n");

            byte power;
            if (!byte.TryParse(txtPower.Text, out power) && _corrupt)
            {
                MsgBoxHelpers.ShowError("Please set Power to a number between 0 and 255 and try again.");
                return false;
            }
            _truePower = (float)power / 255;

            /** SEED PARSING AND RNG SETUP **/
            _friskMode = false;
            _random = new Random();

            int timeSeed = 0;
            string seed = txtSeed.Text.Trim();
            bool textSeed = false;

            if (seed.ToUpper() == "FRISK" || seed.ToUpper() == "KRIS")
                _friskMode = true;

            /* checking # for: Now it will change the corruption every time you press the button */
            else if (seed == "" || txtSeed.Text[0] == '#')
            {
                timeSeed = (int)DateTime.Now.Subtract(_unixTimeZero).TotalSeconds;

                txtSeed.Text = $"#{timeSeed}";
            }
            else if (!int.TryParse(seed, out timeSeed))
            {
                _logWriter.WriteLine($"Text seed - {txtSeed.Text.GetHashCode()}");
                _random = new Random(txtSeed.Text.GetHashCode());
                textSeed = true;
            }

            if (!textSeed)
            {
                _logWriter.WriteLine($"Numeric seed - {timeSeed}");
            }
            _logWriter.WriteLine($"Power - {power}");
            _logWriter.WriteLine($"TruePower - {_truePower}");

            /** ENVIRONMENTAL CHECKS **/
            if (!File.Exists(_dataWin))
            {
                MsgBoxHelpers.ShowError($"You seem to be missing your resource file, {_dataWin}. Make sure you've placed HATE.exe in the proper location.");
                return false;
            }
            else if (!Directory.Exists("HATE_backup"))
            {
                if (!SafeMethods.CreateDirectory("HATE_backup")) { return false; }
                if (!SafeMethods.CopyFile(_dataWin, $"HATE_backup/{_dataWin}")) { return false; }
                if (Directory.Exists("./lang"))
                {
                    if (File.Exists("./lang/lang_en.json") && !SafeMethods.CopyFile("./lang/lang_en.json", "./HATE_backup/lang_en.json")) { return false; };
                    if (File.Exists("./lang/lang_ja.json") && !SafeMethods.CopyFile("./lang/lang_ja.json", "./HATE_backup/lang_ja.json")) { return false; };
                    if (File.Exists("./lang/lang_en_ch1.json") && !SafeMethods.CopyFile("./lang/lang_en_ch1.json", "./HATE_backup/lang_en_ch1.json")) { return false; };
                    if (File.Exists("./lang/lang_ja_ch1.json") && !SafeMethods.CopyFile("./lang/lang_ja_ch1.json", "./HATE_backup/lang_ja_ch1.json")) { return false; };
                }
                _logWriter.WriteLine($"Finished setting up the Data folder.");
            }
            else
            {
                /* restore backup */
                if (!SafeMethods.DeleteFile(_dataWin)) { return false; }
                _logWriter.WriteLine($"Deleted {_dataWin}.");
                if (Directory.Exists("./lang"))
                {
                    if (File.Exists("./lang/lang_en.json") && !SafeMethods.DeleteFile("./lang/lang_en.json")) { return false; }
                    _logWriter.WriteLine($"Deleted ./lang/lang_en.json.");
                    if (File.Exists("./lang/lang_ja.json") && !SafeMethods.DeleteFile("./lang/lang_ja.json")) { return false; }
                    _logWriter.WriteLine($"Deleted ./lang/lang_ja.json.");
                    if (File.Exists("./lang/lang_en_ch1.json") && !SafeMethods.DeleteFile("./lang/lang_en_ch1.json")) { return false; }
                    _logWriter.WriteLine($"Deleted ./lang/lang_en_ch1.json.");
                    if (File.Exists("./lang/lang_ja_ch1.json") && !SafeMethods.DeleteFile("./lang/lang_ja_ch1.json")) { return false; }
                    _logWriter.WriteLine($"Deleted ./lang/lang_ja_ch1.json.");
                }

                if (!SafeMethods.CopyFile($"HATE_backup/{_dataWin}", _dataWin)) { return false; }
                _logWriter.WriteLine($"Copied {_dataWin}.");
                if (Directory.Exists("./lang"))
                {
                    if (File.Exists("./HATE_backup/lang_en_ch1.json") && !SafeMethods.CopyFile("./HATE_backup/lang_en.json", "./lang/lang_en.json")) { return false; }
                    _logWriter.WriteLine($"Copied ./lang/lang_en.json.");
                    if (File.Exists("./HATE_backup/lang_en_ch1.json") && !SafeMethods.CopyFile("./HATE_backup/lang_ja.json", "./lang/lang_ja.json")) { return false; }
                    _logWriter.WriteLine($"Copied ./lang/lang_ja.json.");
                    if (File.Exists("./HATE_backup/lang_en_ch1.json") && !SafeMethods.CopyFile("./HATE_backup/lang_en_ch1.json", "./lang/lang_en_ch1.json")) { return false; }
                    _logWriter.WriteLine($"Copied ./lang/lang_en_ch1.json.");
                    if (File.Exists("./HATE_backup/lang_ja_ch1.json") && !SafeMethods.CopyFile("./HATE_backup/lang_ja_ch1.json", "./lang/lang_ja_ch1.json")) { return false; }
                    _logWriter.WriteLine($"Copied ./lang/lang_ja_ch1.json.");
                }
            }
            await LoadFile(_dataWin);

            return true;
        }

        public void UpdateCorrupt()
        {
            _corrupt = _shuffleGFX || _shuffleText || _hitboxFix || _shuffleFont || _shuffleAudio || _shuffleBG;
            btnCorrupt.Text = Style.GetCorruptLabel(_corrupt);
            btnCorrupt.ForeColor = Style.GetCorruptColor(_corrupt);
        }

        private void chbShuffleText_CheckedChanged(object sender, EventArgs e)
        {
            _shuffleText = chbShuffleText.Checked;
            chbShuffleText.ForeColor = Style.GetOptionColor(_shuffleText);
            UpdateCorrupt();
        }

        private void chbShuffleGFX_CheckedChanged(object sender, EventArgs e)
        {
            _shuffleGFX = chbShuffleGFX.Checked;
            chbShuffleGFX.ForeColor = Style.GetOptionColor(_shuffleGFX);
            UpdateCorrupt();
        }

        private void chbHitboxFix_CheckedChanged(object sender, EventArgs e)
        {
            _hitboxFix = chbHitboxFix.Checked;
            chbHitboxFix.ForeColor = Style.GetOptionColor(_hitboxFix);
            UpdateCorrupt();
        }

        private void chbShuffleFont_CheckedChanged(object sender, EventArgs e)
        {
            _shuffleFont = chbShuffleFont.Checked;
            chbShuffleFont.ForeColor = Style.GetOptionColor(_shuffleFont);
            UpdateCorrupt();
        }

        private void chbShuffleBG_CheckedChanged(object sender, EventArgs e)
        {
            _shuffleBG = chbShuffleBG.Checked;
            chbShuffleBG.ForeColor = Style.GetOptionColor(_shuffleBG);
            UpdateCorrupt();
        }

        private void chbShuffleAudio_CheckedChanged(object sender, EventArgs e)
        {
            _shuffleAudio = chbShuffleAudio.Checked;
            chbShuffleAudio.ForeColor = Style.GetOptionColor(_shuffleAudio);
            UpdateCorrupt();
        }

        private void txtPower_Enter(object sender, EventArgs e)
        {
            txtPower.Text = (txtPower.Text == _defaultPowerText) ? "" : txtPower.Text;
        }

        private void txtPower_Leave(object sender, EventArgs e)
        {
            txtPower.Text = string.IsNullOrWhiteSpace(txtPower.Text) ? _defaultPowerText : txtPower.Text;
        }
    }
    public static class MsgBoxHelpers
    {
        public static MainForm mainForm = null;
        public static DialogResult ShowMessage(string message, MessageBoxButtons buttons, MessageBoxIcon icon, string caption = "HATE-UML")
        {
            return mainForm.Invoke(delegate { return MessageBox.Show(message, caption, buttons, icon); });
        }
        public static DialogResult ShowMessage(string message, string caption = "HATE-UML")
        {
            return mainForm.Invoke(delegate { return MessageBox.Show(message, caption); });
        }
        public static DialogResult ShowWarning(string message, string caption = "HATE-UML")
        {
            return mainForm.Invoke(delegate { return MessageBox.Show(message, caption, MessageBoxButtons.OK, MessageBoxIcon.Warning); });
        }
        public static DialogResult ShowError(string message, string caption = "HATE-UML")
        {
            return mainForm.Invoke(delegate { return MessageBox.Show(message, caption, MessageBoxButtons.OK, MessageBoxIcon.Error); });
        }
    }
}
