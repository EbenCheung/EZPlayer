﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using System.Xml.Serialization;
using EZPlayer.PlayList;
using EZPlayer.Power;
using EZPlayer.Subtitle;
using Microsoft.Win32;
using Vlc.DotNet.Core;
using Vlc.DotNet.Core.Medias;
using Vlc.DotNet.Wpf;

namespace EZPlayer
{
    public partial class MainWindow : Window
    {
        /// <summary>
        /// Used to indicate that the user is currently changing the position (and the position bar shall not be updated). 
        /// </summary>
        private bool m_posUpdateFromVlc;

        private string m_selectedFilePath = null;

        private DispatcherTimer m_activityTimer;

        private DispatcherTimer m_delaySingleClickTimer;

        private SleepBarricade m_sleepBarricade;

        private readonly static string USER_APP_DATA_DIR = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        private readonly static string EZPLAYER_DATA_DIR = Path.Combine(USER_APP_DATA_DIR, "EZPlayer");
        private static readonly string LAST_PLAY_INFO_FILE = Path.Combine(EZPLAYER_DATA_DIR, "lastplay.xml");

        public static DependencyProperty IsPlayingProperty =
            DependencyProperty.Register("IsPlaying", typeof(bool),
            typeof(MainWindow), new FrameworkPropertyMetadata(true,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

        private bool IsPlaying
        {
            get
            {
                return (bool)GetValue(IsPlayingProperty);
            }
            set
            {
                this.Topmost = value;
                if (value)
                {
                    this.m_gridConsole.Opacity = 0.4;
                }
                else
                {
                    this.m_gridConsole.Opacity = 1;
                }

                SetValue(IsPlayingProperty, value);
            }
        }

        public static readonly DependencyProperty VolumeProperty =
            DependencyProperty.Register("Volume", typeof(double),
            typeof(MainWindow),
            new PropertyMetadata(0.0));

        private double Volume
        {
            get
            {
                return (double)GetValue(VolumeProperty);
            }
            set
            {
                m_vlcControl.AudioProperties.Volume = (int)value;
                SetValue(VolumeProperty, value);
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="VlcPlayer"/> class.
        /// </summary>
        public MainWindow()
        {
            InitVlcContext();

            InitializeComponent();

            SetupUserDataDir();

            HookSpaceKeyInput();

            Volume = 100;

            this.MouseWheel += new MouseWheelEventHandler(OnMouseWheel);

            IsPlaying = false;

            InitVlcControl();

            this.Closing += OnClosing;
            this.MouseLeftButtonDown += OnMouseClick;

            InitAutoHideConsoleTimer();

            InitDelaySingleClickTimer();

            m_sleepBarricade = new SleepBarricade(() => IsPlaying);
        }

        private void InitDelaySingleClickTimer()
        {
            m_delaySingleClickTimer = new DispatcherTimer()
            {
                Interval = TimeSpan.FromMilliseconds(500),
                IsEnabled = true
            };
            m_delaySingleClickTimer.Tick += new EventHandler(OnDelayedSingleClickTimer);
        }

        private void InitAutoHideConsoleTimer()
        {
            m_activityTimer = new DispatcherTimer()
            {
                Interval = TimeSpan.FromSeconds(1.5),
                IsEnabled = true
            };
            m_activityTimer.Tick += OnCheckInputStatus;
            m_activityTimer.Start();
        }

        private void InitVlcControl()
        {
            m_vlcControl.VideoProperties.Scale = 2;
            m_vlcControl.PositionChanged += VlcControlOnPositionChanged;
            m_vlcControl.TimeChanged += VlcControlOnTimeChanged;
        }

        private void HookSpaceKeyInput()
        {
            InputManager.Current.PreNotifyInput += new NotifyInputEventHandler(PreNotifyInput);
        }

        private static void SetupUserDataDir()
        {
            if (!Directory.Exists(EZPLAYER_DATA_DIR))
            {
                Directory.CreateDirectory(EZPLAYER_DATA_DIR);
            }
        }

        private static void InitVlcContext()
        {
            // Set libvlc.dll and libvlccore.dll directory path
            VlcContext.LibVlcDllsPath = Path.Combine(Directory.GetCurrentDirectory(), "VLC");

            // Set the vlc plugins directory path
            VlcContext.LibVlcPluginsPath = Path.Combine(VlcContext.LibVlcDllsPath, "plugins");

            /* Setting up the configuration of the VLC instance.
             * You can use any available command-line option using the AddOption function (see last two options). 
             * A list of options is available at 
             *     http://wiki.videolan.org/VLC_command-line_help
             * for example. */

            // Ignore the VLC configuration file
            VlcContext.StartupOptions.IgnoreConfig = true;

            VlcContext.StartupOptions.LogOptions.LogInFile = true;
#if DEBUG
            VlcContext.StartupOptions.LogOptions.Verbosity = VlcLogVerbosities.Debug;
            VlcContext.StartupOptions.LogOptions.ShowLoggerConsole = true;
#else
            //Set the startup options
            VlcContext.StartupOptions.LogOptions.ShowLoggerConsole = false;
            VlcContext.StartupOptions.LogOptions.Verbosity = VlcLogVerbosities.None;
#endif

            // Disable showing the movie file name as an overlay
            VlcContext.StartupOptions.AddOption("--no-video-title-show");

            // The only supporting Chinese font
            VlcContext.StartupOptions.AddOption("--freetype-font=DFKai-SB");

            // Pauses the playback of a movie on the last frame
            //VlcContext.StartupOptions.AddOption("--play-and-pause");

            // Initialize the VlcContext
            VlcContext.Initialize();
        }

        void PreNotifyInput(object sender, NotifyInputEventArgs e)
        {
            if (e.StagingItem.Input.RoutedEvent != Keyboard.KeyDownEvent)
                return;

            var args = e.StagingItem.Input as KeyEventArgs;
            if (args == null || args.Key != Key.Space)
            {
                return;
            }
            args.Handled = true;
            OnBtnPauseClick(null, null);
        }

        void OnDelayedSingleClickTimer(object sender, EventArgs e)
        {
            m_delaySingleClickTimer.Stop();
            OnBtnPauseClick(null, null);
        }

        void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var volume = Volume + e.Delta / 10;
            Volume = MathUtil.Clamp(volume, 0d, 100d);
        }

        /// <summary>
        /// Main window closing event
        /// </summary>
        /// <param name="sender">Event sender. </param>
        /// <param name="e">Event arguments. </param>
        private void OnClosing(object sender, CancelEventArgs e)
        {
            SaveLastPlayInfo();

            // Close the context. 
            VlcContext.CloseAll();
        }

        private void SaveLastPlayInfo()
        {
            if (m_vlcControl.Media != null)
            {
                var item = new HistoryItem()
                {
                    Position = m_vlcControl.Position,
                    FilePath = new Uri(m_vlcControl.Media.MRL).LocalPath,
                    Volume = m_sliderVolume.Value
                };
                using (var stream = File.Open(LAST_PLAY_INFO_FILE, FileMode.Create))
                {
                    new XmlSerializer(typeof(HistoryItem)).Serialize(stream, item);
                }
            }
        }

        /// <summary>
        /// Called if the Play button is clicked; starts the VLC playback. 
        /// </summary>
        /// <param name="sender">Event sender. </param>
        /// <param name="e">Event arguments. </param>
        private void OnBtnPlayClick(object sender, RoutedEventArgs e)
        {
            if (m_vlcControl.Media == null)
            {
                if (TryLoadLastPlayedFile())
                {
                    return;
                }
                this.OnBtnOpenClick(sender, e);
                return;
            }
            else
            {
                DoPlay();
            }
        }

        private bool TryLoadLastPlayedFile()
        {
            if (File.Exists(LAST_PLAY_INFO_FILE))
            {
                try
                {
                    using (var s = File.Open(LAST_PLAY_INFO_FILE, FileMode.Open))
                    {
                        var lastItem = new XmlSerializer(typeof(HistoryItem)).Deserialize(s) as HistoryItem;
                        if (lastItem != null)
                        {
                            m_selectedFilePath = lastItem.FilePath;
                            m_sliderVolume.Value = lastItem.Volume;
                            Play(PlayListUtil.GetPlayList(m_selectedFilePath));
                            UpdatePosition(lastItem.Position);
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Trace.TraceError("Error: {0} \r\n {1}",
                        ex.Message,
                        ex.StackTrace);
                }
            }
            return false;
        }

        /// <summary>
        /// Called if the Pause button is clicked; pauses the VLC playback. 
        /// </summary>
        /// <param name="sender">Event sender. </param>
        /// <param name="e">Event arguments. </param>
        private void OnBtnPauseClick(object sender, RoutedEventArgs e)
        {
            if (m_vlcControl.Media == null)
            {
                return;
            }
            if (m_vlcControl.IsPlaying)
            {
                m_vlcControl.Pause();
            }
            else
            {
                m_vlcControl.Play();
            }
            IsPlaying = !IsPlaying;
        }

        /// <summary>
        /// Called if the Stop button is clicked; stops the VLC playback. 
        /// </summary>
        /// <param name="sender">Event sender. </param>
        /// <param name="e">Event arguments. </param>
        private void OnBtnStopClick(object sender, RoutedEventArgs e)
        {
            IsPlaying = false;
            m_vlcControl.Stop();
            m_sliderPosition.Value = 0;
        }

        /// <summary>
        /// Called if the Open button is clicked; shows the open file dialog to select a media file to play. 
        /// </summary>
        /// <param name="sender">Event sender. </param>
        /// <param name="e">Event arguments. </param>
        private void OnBtnOpenClick(object sender, RoutedEventArgs e)
        {
            if (m_vlcControl.Media != null)
            {
                m_vlcControl.Pause();
                m_vlcControl.Media.ParsedChanged -= OnMediaParsed;
            }

            var openFileDialog = new OpenFileDialog
            {
                Title = "Open media file for playback",
                FileName = "Media File",
                Filter = "All files |*.*"
            };

            // Process open file dialog box results
            if (openFileDialog.ShowDialog() != true)
            {
                if (m_vlcControl.Media != null)
                {
                    m_vlcControl.Play();
                }
                return;
            }

            if (m_vlcControl.Media != null)
            {
                m_vlcControl.Media.ParsedChanged -= OnMediaParsed;
            }

            m_selectedFilePath = openFileDialog.FileName;
            var playList = PlayListUtil.GetPlayList(m_selectedFilePath);
            Play(playList);
        }

        private void Play(List<string> playList)
        {
            SubtitleUtil.PrepareSubtitle(m_selectedFilePath);
            PrepareVLCMediaList(playList);
            m_vlcControl.Media.ParsedChanged += OnMediaParsed;
            DoPlay();
        }

        private void DoPlay()
        {
            m_vlcControl.Play();
            this.IsPlaying = true;
            UpdateTitle();
        }

        private void PrepareVLCMediaList(List<string> playList)
        {
            m_vlcControl.Media = new PathMedia(playList[0]);
            playList.RemoveAt(0);
            playList.ForEach(f => m_vlcControl.Medias.Add(new PathMedia(f)));
        }

        /// <summary>
        /// Volume value changed by the user. 
        /// </summary>
        /// <param name="sender">Event sender. </param>
        /// <param name="e">Event arguments. </param>
        private void SliderVolumeValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            Volume = Convert.ToInt32(m_sliderVolume.Value);
        }

        /// <summary>
        /// Called by <see cref="VlcControl.Media"/> when the media information was parsed. 
        /// </summary>
        /// <param name="sender">Event sending media. </param>
        /// <param name="e">VLC event arguments. </param>
        private void OnMediaParsed(MediaBase sender, VlcEventArgs<int> e)
        {
            m_timeIndicator.Text = string.Format(
                "{0:00}:{1:00}:{2:00}",
                m_vlcControl.Media.Duration.Hours,
                m_vlcControl.Media.Duration.Minutes,
                m_vlcControl.Media.Duration.Seconds);

            Volume = m_vlcControl.AudioProperties.Volume;
        }

        /// <summary>
        /// Called by the <see cref="VlcControl"/> when the media position changed during playback.
        /// </summary>
        /// <param name="sender">Event sennding control. </param>
        /// <param name="e">VLC event arguments. </param>
        private void VlcControlOnPositionChanged(VlcControl sender, VlcEventArgs<float> e)
        {
            m_posUpdateFromVlc = true;
            m_sliderPosition.Value = e.Data;
        }

        private void VlcControlOnTimeChanged(VlcControl sender, VlcEventArgs<TimeSpan> e)
        {
            if (m_vlcControl.Media == null)
                return;
            var duration = m_vlcControl.Media.Duration;
            m_timeIndicator.Text = string.Format(
                "{0:00}:{1:00}:{2:00} / {3:00}:{4:00}:{5:00}",
                e.Data.Hours,
                e.Data.Minutes,
                e.Data.Seconds,
                duration.Hours,
                duration.Minutes,
                duration.Seconds);

            if (IsPlaying != m_vlcControl.IsPlaying)
            {
                IsPlaying = m_vlcControl.IsPlaying;
            }
        }

        /// <summary>
        /// Stop position changing, re-enables updates for the slider by the player. 
        /// </summary>
        /// <param name="sender">Event sender. </param>
        /// <param name="e">Event arguments. </param>
        private void SliderMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var mousePos = e.GetPosition(m_sliderPosition).X;
            var sliderRange = m_sliderPosition.Maximum - m_sliderPosition.Minimum;

            float value = ((float)mousePos / (float)m_sliderPosition.Width) * (float)sliderRange;

            UpdatePosition(value);
        }

        private void UpdatePosition(float value)
        {
            value = MathUtil.Clamp(value, 0.0f, 1.0f);
            m_sliderPosition.Value = value;
        }

        /// <summary>
        /// Change position when the slider value is updated. 
        /// </summary>
        /// <param name="sender">Event sender. </param>
        /// <param name="e">Event arguments. </param>
        private void SliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (m_posUpdateFromVlc)
            {
                m_posUpdateFromVlc = false;
                //Update the current position text when it is in pause
                var duration = m_vlcControl.Media == null ? TimeSpan.Zero : m_vlcControl.Media.Duration;
                var time = TimeSpan.FromMilliseconds(duration.TotalMilliseconds * m_vlcControl.Position);
                m_timeIndicator.Text = string.Format(
                    "{0:00}:{1:00}:{2:00} / {3:00}:{4:00}:{5:00}",
                    time.Hours,
                    time.Minutes,
                    time.Seconds,
                    duration.Hours,
                    duration.Minutes,
                    duration.Seconds);
                return;
            }
            else
            {
                m_vlcControl.Position = (float)e.NewValue;
            }
        }

        private void OnBtnPreviousClick(object sender, RoutedEventArgs e)
        {
            var cur = m_vlcControl.Media;
            if (cur == null)
            {
                OnBtnOpenClick(sender, e);
                return;
            }
            int index = m_vlcControl.Medias.IndexOf(cur);
            if (index > 0)
            {
                m_vlcControl.Previous();
                UpdateTitle();
            }
        }

        private void OnBtnNextClick(object sender, RoutedEventArgs e)
        {
            var cur = m_vlcControl.Media;
            if (cur == null)
            {
                OnBtnOpenClick(sender, e);
                return;
            }
            int index = m_vlcControl.Medias.IndexOf(cur);
            if (index < m_vlcControl.Medias.Count - 1)
            {
                m_vlcControl.Next();
                UpdateTitle();
            }
        }

        private void UpdateTitle()
        {
            Uri uri = new Uri(m_vlcControl.Media.MRL);
            this.Title = Path.GetFileNameWithoutExtension(uri.LocalPath);
        }

        #region Auto Hide

        private struct LASTINPUTINFO
        {
            public int cbSize;
            public uint dwTime;
        }

        [DllImport("User32.dll")]
        private extern static bool GetLastInputInfo(out LASTINPUTINFO plii);

        private void OnCheckInputStatus(object sender, EventArgs e)
        {
            LASTINPUTINFO lastInputInfo = new LASTINPUTINFO();
            lastInputInfo.cbSize = Marshal.SizeOf(typeof(LASTINPUTINFO));
            bool succeed = GetLastInputInfo(out lastInputInfo);
            if (!succeed)
            {
                return;
            }
            TimeSpan idleFor = TimeSpan.FromMilliseconds((long)unchecked((uint)Environment.TickCount - lastInputInfo.dwTime));
            bool isMouseConsoleWnd = IsMouseInControl(this);
            if (!isMouseConsoleWnd || idleFor > TimeSpan.FromSeconds(1.5))
            {
                if (m_vlcControl.IsPlaying)
                {
                    this.m_gridConsole.Visibility = Visibility.Hidden;
                    Mouse.OverrideCursor = Cursors.None;
                }
            }
            else
            {
                Mouse.OverrideCursor = null;
                if (isMouseConsoleWnd && m_gridConsole.Visibility != Visibility.Visible)
                {
                    m_gridConsole.Visibility = Visibility.Visible;
                }
            }
            RestartInputMonitorTimer();
        }

        private bool IsMouseInControl(IInputElement control)
        {
            var point = Mouse.GetPosition(control);
            return point.X >= 0 && point.Y >= 0;
        }

        private void RestartInputMonitorTimer()
        {
            m_activityTimer.Stop();
            m_activityTimer.Start();
        }
        
        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (this.IsMouseInControl(this))
            {
                if (Mouse.OverrideCursor == Cursors.None)
                {
                    Mouse.OverrideCursor = null;
                }
                m_gridConsole.Visibility = Visibility.Visible;
            }
        }
        #endregion

        #region FullScreen
        void OnMouseClick(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                m_delaySingleClickTimer.Stop();
                ToggleFullScreenMode();
            }
            else if (e.ClickCount == 1)
            {
                m_delaySingleClickTimer.Start();
            }
        }

        private void ToggleFullScreenMode()
        {
            if (IsInFullScreenMode())
            {
                SwitchToNormalMode();
            }
            else
            {
                SwitchToFullScreenMode();
            }
        }

        private void SwitchToFullScreenMode()
        {
            this.WindowStyle = WindowStyle.None;

            // workaround to hide taskbar when swith from maximised to fullscreen
            this.WindowState = WindowState.Normal;

            this.WindowState = WindowState.Maximized;
        }

        private void SwitchToNormalMode()
        {
            this.WindowStyle = WindowStyle.SingleBorderWindow;
            this.WindowState = WindowState.Normal;
        }

        private bool IsInFullScreenMode()
        {
            return this.WindowState == WindowState.Maximized &&
                                this.WindowStyle == WindowStyle.None;
        }
        #endregion

        private void OnDropFile(object sender, DragEventArgs e)
        {
            if (e.Data is DataObject && ((DataObject)e.Data).ContainsFileDropList())
            {
                var fileList = (e.Data as DataObject).GetFileDropList();
                if (fileList.Count == 1)
                {
                    m_selectedFilePath = fileList[0];
                    Play(PlayListUtil.GetPlayList(m_selectedFilePath));
                }
                else if (fileList.Count > 1)
                {
                    var sortedFileList = fileList.Cast<string>().OrderBy(s => s).ToList();
                    m_selectedFilePath = sortedFileList[0];
                    Play(sortedFileList);
                }
            }
        }

        private void OnBtnForwardClick(object sender, RoutedEventArgs e)
        {
            var newValue = m_vlcControl.Position + 0.001f;
            UpdatePosition(newValue);
        }

        private void OnBtnRewindClick(object sender, RoutedEventArgs e)
        {
            var newValue = m_vlcControl.Position - 0.001f;
            UpdatePosition(newValue);
        }

        private void OnBtnFullScreenClick(object sender, RoutedEventArgs e)
        {
            ToggleFullScreenMode();
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (ShortKeys.IsRewindShortKey(e))
            {
                OnBtnRewindClick(null, null);
            }
            if (ShortKeys.IsForwardShortKey(e))
            {
                OnBtnForwardClick(null, null);
            }

            if (ShortKeys.IsDecreaseVolumeShortKey(e))
            {
                var volume = Volume - 12;
                Volume = MathUtil.Clamp(volume, 0d, 100d);
            }
            if (ShortKeys.IsIncreaseVolumeShortKey(e))
            {
                var volume = Volume + 12;
                Volume = MathUtil.Clamp(volume, 0d, 100d);
            }

            if (ShortKeys.IsFullScreenShortKey(e))
            {
                ToggleFullScreenMode();
            }

            if (ShortKeys.IsPauseShortKey(e))
            {
                OnBtnPauseClick(null, null);
            }
        }
    }
}