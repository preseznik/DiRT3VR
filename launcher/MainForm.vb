Imports System.Drawing
Imports System.Windows.Forms

Public Class MainForm
    Inherits LauncherForm
    Private Shared ReadOnly BuildVersion As String = GetType(MainForm).Assembly.GetName().Version.ToString(3)
    Private context As InstallContext
    Private settings As LauncherSettings
    Private savedSettings As String
    Private profilePanel As SettingsProfilePanel
    Private busy As Boolean
    Private resourcesDisposed As Boolean
    Private windowReady As Boolean
    Private lastWindowState As FormWindowState = FormWindowState.Normal
    Private launchProgress As LaunchProgressMonitor
    Private interactive As InteractiveSession
    Private desktopStartup As DesktopStartupSession
    Private ReadOnly skipStartupMovies As New CheckBox With {.Name = "SkipStartupMovies", .Text = "Skip startup logo movies (experimental)", .AutoSize = True}
    Private ReadOnly recenterButton As New Button With {.Name = "RecenterKey", .AutoSize = True, .AccessibleName = "Recenter key"}
    Private ReadOnly recenterHint As New Label With {.Name = "RecenterHint", .AutoSize = True, .UseMnemonic = False, .Margin = New Padding(0, 4, 0, 12)}
    Private recenterKey As Integer = RecenterShortcut.DefaultKey, recenterModifiers As Integer
    Private capturingRecenter As Boolean
    Private ReadOnly experimental As New CheckBox With {.Name = "ExperimentalVR", .Text = "Enable experimental VR (VDXR)", .AutoSize = True}
    Private ReadOnly tabs As New ModernTabs With {.Name = "LauncherTabs", .Dock = DockStyle.Fill}
    Private ReadOnly state As New Label With {.Name = "SessionStatus", .AutoSize = True}
    Private ReadOnly notice As New Label With {.Name = "Notice", .AutoSize = True, .Margin = New Padding(0, 12, 0, 8)}
    Private ReadOnly gameFolder As New TextBox With {.Name = "GameFolder", .ReadOnly = True}
    Private ReadOnly graphicsPath As New TextBox With {.Name = "GraphicsPath", .ReadOnly = True}
    Private ReadOnly runtime As New ComboBox With {.Name = "RuntimeChoice", .DropDownStyle = ComboBoxStyle.DropDownList}
    Private ReadOnly manifest As New TextBox With {.Name = "RuntimePath"}
    Private ReadOnly vdxrManifest As New TextBox With {.Name = "VdxrRuntimePath"}
    Private ReadOnly modern As New AppearanceSwitch()
    Private ReadOnly logging As New CheckBox With {.Name = "DiagnosticLogging", .Text = "On", .AutoSize = True}
    Private ReadOnly renderScale As New ValueSlider("VrRenderScale", 50, 300, 100, "%", recommendedValue:=100)
    Private ReadOnly desktopResolution As New ComboBox With {.Name = "DesktopResolution", .DropDownStyle = ComboBoxStyle.DropDownList}
    Private ReadOnly desktopMode As New ComboBox With {.Name = "DesktopDisplayMode", .DropDownStyle = ComboBoxStyle.DropDownList}
    Private ReadOnly desktopVSync As New ComboBox With {.Name = "DesktopVSync", .DropDownStyle = ComboBoxStyle.DropDownList}
    Private headsetResolution As HeadsetResolution
    Private ReadOnly resolutionSummary As New Label With {.Name = "VrResolutionSummary", .AutoSize = True}
    Private ReadOnly refreshHeadset As New Button With {.Name = "RefreshHeadset", .Text = "Refresh headset resolution", .AutoSize = True}
    Private ReadOnly probe As New Button With {.Name = "CheckRuntime", .Text = "Check runtime and headset", .AutoSize = True}
    Private ReadOnly probeStatus As New Label With {.Name = "ProbeStatus", .Text = "No runtime check has been performed in this session.", .AutoSize = True}
    Private ReadOnly diagnostics As New TextBox With {.Name = "Diagnostics", .ReadOnly = True, .Multiline = True, .ScrollBars = ScrollBars.Vertical, .Height = 230}
    Private ReadOnly desktop As New Button With {.Name = "LaunchDesktop", .Text = "Launch", .AutoSize = True}
    Private ReadOnly vr As New Button With {.Name = "LaunchVR", .Text = "Launch VR", .AutoSize = True, .Enabled = False}
    Private ReadOnly save As New Button With {.Name = "SaveSettings", .Text = "Save settings", .AutoSize = True}
    Private ReadOnly recover As New Button With {.Name = "RestoreFiles", .Text = "Restore original files", .AutoSize = True}
    Private ReadOnly logs As New Button With {.Name = "OpenLogs", .Text = "Open logs", .AutoSize = True}
    Private ReadOnly tips As New ToolTip With {.AutoPopDelay = 20000}
    Private ReadOnly timer As New Timer With {.Interval = 1500}
    Private ReadOnly lifetime As New System.Threading.CancellationTokenSource()
    Private ReadOnly headers As New List(Of SectionHeader)
    Private collapsedSectionTitles As String

    Public Sub New(value As InstallContext)
        context = value
        Try
            settings = LauncherSettings.Load(context)
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is System.Text.Json.JsonException
            settings = New LauncherSettings()
            notice.Text = "Stored preferences could not be loaded: " & ex.Message & " They remain unchanged until you save."
        End Try
        LauncherAppearance.Modern = settings.ModernInterface
        Text = "DiRT3VR " & BuildVersion & " — Research build"
        Font = New Font("Segoe UI", 10)
        AutoScaleMode = AutoScaleMode.Dpi
        MinimumSize = New Size(560, 540)
        ClientSize = New Size(900, 760)
        StartPosition = FormStartPosition.CenterScreen
        Dim layout As New TableLayoutPanel With {.Dock = DockStyle.Fill, .Padding = New Padding(20), .ColumnCount = 1, .RowCount = 5}
        layout.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        layout.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        layout.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        layout.RowStyles.Add(New RowStyle(SizeType.Percent, 100))
        layout.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        layout.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        Dim header As New TableLayoutPanel With {.Dock = DockStyle.Fill, .ColumnCount = 2, .AutoSize = True, .Margin = New Padding(0, 0, 0, 12)}
        header.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        header.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
        header.Controls.Add(New Label With {.Text = "DiRT 3 VR", .Font = New Font(Font.FontFamily, 20, FontStyle.Bold), .AutoSize = True}, 0, 0)
        Dim help As New Button With {.Name = "HelpAbout", .Text = "?", .AccessibleName = "Help / About", .Size = New Size(36, 36)}
        AddHandler help.Click, Sub() ShowHelp()
        header.Controls.Add(help, 1, 0)
        layout.Controls.Add(header, 0, 0)
        state.Margin = New Padding(0, 0, 0, 12)
        layout.Controls.Add(state, 0, 1)
        layout.Controls.Add(tabs, 0, 2)
        BuildLauncher() : BuildGraphics() : BuildControls() : BuildSettings() : BuildAdvanced()
        headers.AddRange(FindHeaders(tabs))
        For Each heading In headers
            AddHandler heading.CollapsedChanged, Sub() collapsedSectionTitles = WindowPreferences.JoinSections(headers.Where(Function(h) h.Collapsed).Select(Function(h) h.Title))
        Next
        ApplyCollapsedSections(WindowPreferences.Load(context).CollapsedSections)
        layout.Controls.Add(notice, 0, 3)
        layout.Controls.Add(New LauncherFooter(desktop, vr, save, recover, logs), 0, 4)
        Controls.Add(layout)
        ApplySettings()
        RememberSavedSettings()
        AddHandler desktop.Click, Async Sub() Await ActionAsync(Async Function()
                                                                   If InstallContext.GameRunning() Then
                                                                       Dim window = GameWindow.Find(context)
                                                                       If window = IntPtr.Zero Then Throw New IOException("DiRT 3 is still loading or its game window is unavailable. Check the taskbar or Open logs.")
                                                                       GameWindow.Activate(window, explicitRequest:=True)
                                                                       Return
                                                                   End If
                                                                   Dim selected = CurrentSettings()
                                                                   Dim borderlessBounds = If(selected.DesktopDisplayMode = "borderless", DesktopDisplay.MainBounds(), Rectangle.Empty)
                                                                   Dim game As Process
                                                                   Try
                                                                       If selected.SkipStartupMovies Then
                                                                           desktopStartup = Await DesktopStartupSession.StartAsync(context, selected, borderlessBounds.Size)
                                                                           game = desktopStartup.Game
                                                                       Else
                                                                           game = Await Task.Run(Function() Session.LaunchDesktop(context, selected, borderlessBounds.Size))
                                                                       End If
                                                                   Catch ex As Exception
                                                                       Session.WriteLaunchStatus(context, "failed", ex.Message, 0, DateTime.UtcNow)
                                                                       Throw
                                                                   End Try
                                                                   Try
                                                                       If IsDisposed Then
                                                                           If desktopStartup Is Nothing Then game.Dispose()
                                                                           Return
                                                                       End If
                                                                       settings = selected
                                                                       RememberSavedSettings()
                                                                       launchProgress = New LaunchProgressMonitor(Me, game, context, borderlessBounds:=borderlessBounds, disposeProcess:=desktopStartup Is Nothing)
                                                                       AddHandler launchProgress.Completed, Sub(message As String)
                                                                                                                 launchProgress = Nothing
                                                                                                                 notice.Text = message
                                                                                                                 RefreshState()
                                                                                                             End Sub
                                                                       notice.Text = If(desktopStartup Is Nothing, "Starting DiRT 3… Closing this launcher leaves the game running.", "Starting DiRT 3… Keep the launcher open for startup file restoration after exit.")
                                                                       launchProgress.Start()
                                                                   Finally
                                                                       If desktopStartup IsNot Nothing Then ObserveDesktopStartup()
                                                                   End Try
                                                               End Function)
        AddHandler save.Click, Sub() SafeAction(Sub()
                                                   CurrentSettings().Save(context)
                                                   RememberSavedSettings()
                                                   notice.Text = "Launcher settings saved. Settings profiles were not changed."
                                               End Sub)
        AddHandler vr.Click, Async Sub() Await ActionAsync(Async Function()
                                                              If interactive IsNot Nothing Then
                                                                  notice.Text = Await interactive.StopAsync()
                                                                  Return
                                                              End If
                                                              Dim selected = CurrentSettings()
                                                              interactive = Await InteractiveSession.StartAsync(context, selected)
                                                              settings = selected
                                                              RememberSavedSettings()
                                                              launchProgress = New LaunchProgressMonitor(Me, interactive.Game, context, vr:=True)
                                                              AddHandler launchProgress.Completed, Sub(message As String)
                                                                                                        launchProgress = Nothing
                                                                                                        notice.Text = message
                                                                                                        RefreshState()
                                                                                                    End Sub
                                                              notice.Text = "Starting experimental VR. The launcher stays open to restore original files when you quit."
                                                              launchProgress.Start()
                                                              ObserveInteractive()
                                                          End Function)
        AddHandler recover.Click, Async Sub() Await ActionAsync(Async Function()
                                                                   Using lease = InteractiveSession.AcquireLease(context)
                                                                   Dim restored = Await RecoveryWorker.RecoverAsync(context)
                                                                   notice.Text = If(restored, "Owned session files were restored.", "No launcher file recovery is pending. No game files were changed.")
                                                                   End Using
                                                               End Function)
        AddHandler logs.Click, Sub() SafeAction(Sub()
                                                   Directory.CreateDirectory(context.UserRoot)
                                                   Process.Start(New ProcessStartInfo(context.UserRoot) With {.UseShellExecute = True})?.Dispose()
                                               End Sub)
        AddHandler probe.Click, Async Sub() Await RefreshHeadsetAsync()
        AddHandler refreshHeadset.Click, Async Sub() Await RefreshHeadsetAsync()
        AddHandler renderScale.ValueChanged, Sub() UpdateResolutionLabels()
        For Each input As TextBox In {manifest, vdxrManifest, gameFolder}
            AddHandler input.TextChanged, Sub() InvalidateHeadset()
        Next
        AddHandler modern.CheckedChanged, Sub()
                                              LauncherAppearance.Modern = modern.Checked
                                              LauncherAppearance.Apply(Me)
                                          End Sub
        AddHandler runtime.SelectedIndexChanged, Sub()
                                                     manifest.Enabled = runtime.SelectedIndex = 1 AndAlso Not busy
                                                     vdxrManifest.Enabled = runtime.SelectedIndex = 0 AndAlso Not busy
                                                     InvalidateHeadset()
                                                     RefreshState()
                                                 End Sub
        AddHandler experimental.CheckedChanged, Sub() RefreshState()
        AddHandler timer.Tick, Sub() RefreshState()
        AddHandler SizeChanged, Sub()
                                   state.MaximumSize = New Size(Math.Max(1, ClientSize.Width - 40), 0)
                                   RefreshLabelWidths(Me)
                               End Sub
        AddHandler Resize, Sub()
                               If windowReady AndAlso WindowState <> FormWindowState.Minimized Then lastWindowState = WindowState
                           End Sub
        AddHandler Shown, Sub() FitInitialWindow()
        tips.SetToolTip(desktop, "Start the game with your desktop graphics choices and saved driving controls. No headset is required.")
        tips.SetToolTip(save, "Keep the current controls as the launcher's settings for next time, as Launch does. Settings profiles are not changed.")
        tips.SetToolTip(probe, "Check OpenXR and headset access without starting DiRT 3. This does not validate stereoscopic game rendering.")
        tips.SetToolTip(skipStartupMovies, "Skips the AMD and DiRT 3 logo movies in desktop and VR launches. Copyright and Press Start remain. Keep this launcher open until the game exits so the original startup file can be restored.")
        UpdateResolutionLabels()
        RefreshState() : LoadDiagnostics()
        timer.Start()
    End Sub

    Private Function Page(title As String) As VerticalStack
        Dim tabPage As New TabPage(title) With {.Padding = New Padding(16), .AutoScroll = True, .UseVisualStyleBackColor = False}
        Dim content = Stack()
        tabPage.Controls.Add(content) : tabs.TabPages.Add(tabPage)
        AddHandler tabPage.SizeChanged, Sub()
                                         content.Width = Math.Max(1, tabPage.ClientSize.Width - tabPage.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth)
                                         RefreshLabelWidths(content)
                                     End Sub
        Return content
    End Function

    Private Shared Iterator Function FindHeaders(parent As Control) As IEnumerable(Of SectionHeader)
        For Each child As Control In parent.Controls
            If TypeOf child Is SectionHeader Then Yield DirectCast(child, SectionHeader)
            For Each nested In FindHeaders(child)
                Yield nested
            Next
        Next
    End Function

    Public ReadOnly Property SectionHeaders As IReadOnlyList(Of SectionHeader)
        Get
            Return headers
        End Get
    End Property

    ' Collapsed section titles as saved with the window size; Nothing when all are open.
    Public ReadOnly Property CollapsedSections As String
        Get
            Return collapsedSectionTitles
        End Get
    End Property

    Public Sub ApplyCollapsedSections(saved As String)
        Dim titles = WindowPreferences.SplitSections(saved)
        For Each header In headers
            header.Collapsed = titles.Contains(header.Title)
        Next
    End Sub

    Private Sub FitInitialWindow()
        Dim work = Screen.FromControl(Me).WorkingArea
        Dim saved = WindowPreferences.Load(context)
        MinimumSize = New Size(Math.Min(Px(Me, 560), work.Width), Math.Min(Px(Me, 540), work.Height))
        Size = saved.Fit(work.Size, DeviceDpi, Size)
        ' Honor explicit placement (including offscreen test forms).
        If StartPosition <> FormStartPosition.Manual Then Location = New Point(work.Left + (work.Width - Width) \ 2, work.Top + (work.Height - Height) \ 2)
        windowReady = True
        If saved.Maximized Then WindowState = FormWindowState.Maximized
    End Sub

    Private Sub BuildLauncher()
        Dim content = Page("Launcher")
        Dim columns As New ResponsiveColumns()
        content.Controls.Add(columns)
        Dim launch = Section(columns.First, "Launch mode")
        launch.Controls.Add(Info("Normal Launch", True))
        launch.Controls.Add(Info("Choose your career, event, car and track in DiRT 3. Launch uses the original desktop game and its saved settings."))
        Dim startup = Section(columns.First, "Startup")
        startup.Controls.Add(skipStartupMovies)
        startup.Controls.Add(Info("Applies to Launch and Launch VR. Copyright and Press Start remain. Keep the launcher open until the game closes; it restores the original startup file. Leave this option off for multiplayer until compatibility is checked."))
        Dim availability = Section(columns.Second, "VR availability")
        availability.Controls.Add(Info("Experimental VR is available with VDXR when enabled in Advanced. All rendered 3D camera views use stereo and headset tracking, including cockpit, bonnet, bumper, chase and replay. Loading screens and movies use a virtual screen."))
        availability.Controls.Add(Info("Interactive play and camera transitions are still being validated. SteamVR interactive launch is unavailable pending orientation validation."))
        content.Controls.Add(HelpLink(AddressOf ShowHelp))
    End Sub

    Private Sub BuildGraphics()
        Dim content = Page("Graphics")
        Dim columns As New ResponsiveColumns() : content.Controls.Add(columns)
        Dim desktopSettings = Section(columns.First, "Desktop")
        desktopMode.Items.AddRange({"Use game setting", "Windowed", "Fullscreen", "Borderless"})
        desktopVSync.Items.AddRange({"Use game setting", "Off", "On"})
        Field(desktopSettings, "Display mode", desktopMode)
        Field(desktopSettings, "Resolution", desktopResolution)
        Field(desktopSettings, "VSync", desktopVSync)
        For Each box In {desktopMode, desktopResolution, desktopVSync}
            StyleChoice(box, desktopSettings)
        Next
        AddHandler desktopMode.SelectedIndexChanged, Sub() desktopResolution.Enabled = desktopMode.SelectedIndex <> 3
        desktopSettings.Controls.Add(Info("Applies when you click Launch. Use game setting leaves that option unchanged. Desktop changes stay in the game's graphics file; a backup is saved before each change. Save settings only saves launcher preferences."))
        desktopSettings.Controls.Add(Info("Borderless fills the main display at its current resolution. Keep this launcher open until startup finishes. VR uses its own resolution, window mode and VSync settings."))
        Dim vrSettings = Section(columns.Second, "VR rendering")
        vrSettings.Controls.Add(Info(ExperimentalFiles.ProfileDescription))
        Field(vrSettings, "VR render scale", renderScale)
        vrSettings.Controls.Add(Info("100% renders at the connected headset's recommended resolution per eye. Output stays at that recommendation; this slider changes scene detail. Applies on the next VR launch. Unsupported sizes are refused, and original game settings are restored afterward."))
        vrSettings.Controls.Add(resolutionSummary)
        vrSettings.Controls.Add(refreshHeadset)
        vrSettings.Controls.Add(Info("Changing game graphics during VR can leave a recovery conflict; the launcher preserves your changes and the original backup."))
        Field(content, "Game graphics file", graphicsPath)
    End Sub

    Private Sub InvalidateHeadset()
        headsetResolution = Nothing
        UpdateResolutionLabels()
    End Sub

    Private Sub UpdateResolutionLabels()
        resolutionSummary.Text = If(headsetResolution Is Nothing,
            "Connect your headset and refresh to see the recommended and rendered dimensions. The selected runtime is checked again before every VR launch.",
            headsetResolution.Summary(renderScale.Value))
        RefreshLabelWidths(Me)
    End Sub

    Private Async Function RefreshHeadsetAsync() As Task
        Await ActionAsync(Async Function()
                              InvalidateHeadset()
                              resolutionSummary.Text = "Reading connected headset resolution…"
                              Try
                                  headsetResolution = Await VrRuntime.ReadHeadsetAsync(context, CurrentSettings(), lifetime.Token)
                                  UpdateResolutionLabels()
                                  probeStatus.Text = "Headset resolution detected. No game or VR session was started."
                                  LoadDiagnostics()
                              Catch
                                  resolutionSummary.Text = "Headset resolution unavailable. Connect through the selected runtime and refresh."
                                  Throw
                              End Try
                          End Function)
    End Function

    Private Sub BuildControls()
        Dim content = Page("Controls")
        Dim general = Section(content, "General")
        general.Controls.Add(Info("Use your existing keyboard, controller or wheel bindings in DiRT 3. During experimental VR, closing this launcher minimizes it until the game exits and original files are restored."))
        Dim liveInput As New Button With {.Name = "LiveInput", .Text = "Live input and force feedback…", .AutoSize = True}
        general.Controls.Add(liveInput)
        general.Controls.Add(Info("Shows your wheel, pedals, shifter and controllers as Windows sees them, and tests rumble or force feedback. It doesn't change DiRT 3's controls."))
        AddHandler liveInput.Click, Sub()
                                        Using monitor As New DrivingInputMonitor(DrivingInput.LibraryPath(context))
                                            monitor.ShowDialog(Me)
                                        End Using
                                    End Sub
        Dim vrControls = Section(content, "VR and seat controls")
        Field(vrControls, "Recenter key", recenterButton)
        vrControls.Controls.Add(recenterHint)
        vrControls.Controls.Add(Info("Head tracking applies to every rendered 3D camera view, including replay. Per-car seat adjustment is not available yet."))
        AddHandler recenterButton.Click, Sub()
                                             capturingRecenter = True
                                             UpdateRecenterLabel()
                                             notice.Text = "Press the new recenter key, with Ctrl, Alt or Shift if you like. Esc cancels."
                                         End Sub
        AddHandler recenterButton.LostFocus, Sub()
                                                 If Not capturingRecenter Then Return
                                                 capturingRecenter = False : UpdateRecenterLabel()
                                             End Sub
        tips.SetToolTip(recenterButton, "Click, then press the key or key combination that recenters the headset view while DiRT 3 is in front.")
        UpdateRecenterLabel()
    End Sub

    Private Sub UpdateRecenterLabel()
        Dim keyText = RecenterShortcut.Describe(recenterKey, recenterModifiers)
        recenterButton.Text = If(capturingRecenter, "Press a key…", keyText)
        recenterHint.Text = "Press " & keyText & " while DiRT 3 is the foreground application to recenter the view. DiRT 3 also receives the key, so pick one the game doesn't use."
    End Sub

    Protected Overrides Function ProcessCmdKey(ByRef msg As Message, keyData As Keys) As Boolean
        If CaptureRecenterKey(keyData) Then Return True
        Return MyBase.ProcessCmdKey(msg, keyData)
    End Function

    ' Handles one key press while the recenter button waits for a key (public for the launcher tests).
    Public Function CaptureRecenterKey(keyData As Keys) As Boolean
        If Not capturingRecenter Then Return False
        If RecenterShortcut.IsModifierOnly(keyData) Then Return True
        If keyData = Keys.Escape Then
            capturingRecenter = False : UpdateRecenterLabel()
            notice.Text = "Recenter key unchanged."
            Return True
        End If
        Dim key = CInt(keyData And Keys.KeyCode), modifiers = RecenterShortcut.Modifiers(keyData)
        If Not RecenterShortcut.Valid(key, modifiers) Then
            notice.Text = RecenterShortcut.Describe(key, modifiers) & " can't be used. Choose another key; Windows shortcuts such as Alt+Tab are reserved. Esc cancels."
            Return True
        End If
        recenterKey = key : recenterModifiers = modifiers : capturingRecenter = False
        UpdateRecenterLabel()
        notice.Text = "Recenter key set to " & RecenterShortcut.Describe(key, modifiers) & ". Save settings or Launch to keep it."
        Return True
    End Function

    Private Sub BuildSettings()
        Dim content = Page("Settings")
        Dim appearance = Section(content, "Appearance")
        Field(appearance, "Modern interface", modern)
        Dim profiles = Section(content, "Settings profiles")
        profilePanel = New SettingsProfilePanel(context, AddressOf CurrentSettings,
            Function(profile) LoadSettingsProfile(profile, AddressOf ConfirmReplaceSettings))
        profiles.Controls.Add(profilePanel)
        Dim locations = Section(content, "Locations")
        Field(locations, "Game folder", gameFolder)
        Dim browse As New Button With {.Name = "BrowseGame", .Text = "Choose game folder…", .AutoSize = True}
        locations.Controls.Add(browse)
        AddHandler browse.Click, Sub() SafeAction(Sub()
                                                    If interactive IsNot Nothing OrElse desktopStartup IsNot Nothing OrElse InstallContext.GameRunning() Then Throw New IOException("Close DiRT 3 before changing installations.")
                                                    Using picker As New FolderBrowserDialog With {.Description = "Select DiRT 3 Complete Edition", .SelectedPath = context.GameRoot, .UseDescriptionForTitle = True}
                                                        If picker.ShowDialog(Me) <> DialogResult.OK Then Return
                                                        Dim chosen As New InstallContext(picker.SelectedPath, package:=context.PackageRoot)
                                                        chosen.ValidateGame()
                                                        Dim loaded = LauncherSettings.Load(chosen)
                                                        If HasUnsavedSettings() AndAlso Not ConfirmReplaceSettings() Then Return
                                                        context = chosen : settings = loaded : ApplySettings() : LoadDiagnostics()
                                                        RememberSavedSettings() : profilePanel.SetContext(context)
                                                        notice.Text = "Selected game folder. Settings are separate for each installation."
                                                    End Using
                                                End Sub)
        Dim runtimes = Section(content, "VR runtime")
        runtime.Items.AddRange({"VDXR", "SteamVR"})
        StyleChoice(runtime, Me)
        Field(runtimes, "Runtime", runtime)
        Field(runtimes, "VDXR x86 manifest", vdxrManifest)
        Field(runtimes, "SteamVR x86 manifest", manifest)
        Dim browseRuntime As New Button With {.Name = "BrowseRuntime", .Text = "Browse selected runtime…", .AutoSize = True}
        runtimes.Controls.Add(browseRuntime)
        AddHandler browseRuntime.Click, Sub()
                                            Dim filename = If(runtime.SelectedIndex = 1, "steamxr_win32.json", "virtualdesktop-openxr-32.json")
                                            Using picker As New OpenFileDialog With {.Filter = "32-bit runtime|" & filename, .FileName = filename}
                                                If picker.ShowDialog(Me) = DialogResult.OK Then
                                                    If runtime.SelectedIndex = 1 Then manifest.Text = picker.FileName Else vdxrManifest.Text = picker.FileName
                                                End If
                                            End Using
                                        End Sub
        runtimes.Controls.Add(Info("For VDXR, connect through Virtual Desktop. SteamVR runtime checks remain available, but interactive SteamVR launch is awaiting orientation validation. The selected 32-bit runtime is used only by the launched process; your system OpenXR default stays unchanged."))
        runtimes.Controls.Add(probe) : runtimes.Controls.Add(probeStatus)
        Dim options = Section(content, "Diagnostics")
        options.Controls.Add(Info("Startup results and failures are always recorded in Open logs."))
    End Sub

    Private Sub BuildAdvanced()
        Dim content = Page("Advanced")
        Dim capability = Section(content, "Implementation status")
        capability.Controls.Add(experimental)
        capability.Controls.Add(Info("Opt in to the experimental VDXR session. Version 0.1.5 has user-confirmed interactive VR with cockpit and external views. Full camera, effects and sustained-driving validation is still in progress. A runtime check alone does not establish game support."))
        capability.Controls.Add(Info("Keep the launcher open during VR. Use Stop VR / quit or quit in the game; original files are restored after the game closes."))
        capability.Controls.Add(Info("Supported executable SHA-256: " & InstallContext.SupportedHash))
        Dim status = Section(content, "Latest diagnostic report")
        status.Controls.Add(diagnostics)
        Dim refresh As New Button With {.Name = "RefreshDiagnostics", .Text = "Refresh report", .AutoSize = True}
        status.Controls.Add(refresh)
        AddHandler refresh.Click, Sub() LoadDiagnostics()
    End Sub

    Private Sub ApplySettings()
        gameFolder.Text = context.GameRoot : graphicsPath.Text = context.GraphicsPath
        modern.Checked = settings.ModernInterface
        runtime.SelectedIndex = If(settings.RuntimeMode = "steamvr", 1, 0)
        manifest.Text = settings.SteamRuntimeManifest : manifest.Enabled = runtime.SelectedIndex = 1
        vdxrManifest.Text = If(String.IsNullOrWhiteSpace(settings.VdxrRuntimeManifest), Discovery.VdxrRuntime(), settings.VdxrRuntimeManifest)
        vdxrManifest.Enabled = runtime.SelectedIndex = 0
        logging.Checked = settings.DiagnosticLogging
        experimental.Checked = settings.ExperimentalVrEnabled
        skipStartupMovies.Checked = settings.SkipStartupMovies
        recenterKey = settings.RecenterKey : recenterModifiers = settings.RecenterModifiers : capturingRecenter = False : UpdateRecenterLabel()
        renderScale.Value = settings.VrRenderScale
        desktopMode.SelectedIndex = Array.IndexOf({"game", "windowed", "fullscreen", "borderless"}, settings.DesktopDisplayMode)
        desktopVSync.SelectedIndex = Array.IndexOf({"game", "off", "on"}, settings.DesktopVSync)
        desktopResolution.Items.Clear()
        Dim chosen = New Size(settings.DesktopWidth, settings.DesktopHeight)
        Dim sizes = DesktopDisplay.Resolutions().Append(chosen).Append(Size.Empty).Distinct().OrderBy(Function(s) s.Width).ThenBy(Function(s) s.Height)
        For Each size As Size In sizes
            Dim item As New DesktopResolutionChoice(size)
            desktopResolution.Items.Add(item)
            If size = chosen Then desktopResolution.SelectedItem = item
        Next
        LauncherAppearance.Modern = modern.Checked : LauncherAppearance.Apply(Me)
    End Sub

    Public Function CurrentSettings() As LauncherSettings
        Dim resolution = TryCast(desktopResolution.SelectedItem, DesktopResolutionChoice)
        Return New LauncherSettings With {.ModernInterface = modern.Checked, .RuntimeMode = If(runtime.SelectedIndex = 1, "steamvr", "vdxr"), .SteamRuntimeManifest = manifest.Text, .VdxrRuntimeManifest = vdxrManifest.Text, .DiagnosticLogging = logging.Checked, .ExperimentalVrEnabled = experimental.Checked, .VrRenderScale = renderScale.Value,
            .DesktopWidth = If(resolution Is Nothing, 0, resolution.Size.Width), .DesktopHeight = If(resolution Is Nothing, 0, resolution.Size.Height),
            .DesktopDisplayMode = {"game", "windowed", "fullscreen", "borderless"}(Math.Max(0, desktopMode.SelectedIndex)), .DesktopVSync = {"game", "off", "on"}(Math.Max(0, desktopVSync.SelectedIndex)), .SkipStartupMovies = skipStartupMovies.Checked,
            .RecenterKey = recenterKey, .RecenterModifiers = recenterModifiers}
    End Function

    Private Sub RememberSavedSettings()
        savedSettings = System.Text.Json.JsonSerializer.Serialize(CurrentSettings())
    End Sub

    Public Function HasUnsavedSettings() As Boolean
        Return savedSettings IsNot Nothing AndAlso savedSettings <> System.Text.Json.JsonSerializer.Serialize(CurrentSettings())
    End Function

    Private Function ConfirmReplaceSettings() As Boolean
        Return MessageBox.Show(Me, "Replace the unsaved changes in the launcher controls? Choose Cancel to keep them; you can save them as a profile first.",
            "Unsaved launcher settings", MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) = DialogResult.OK
    End Function

    Public Function LoadSettingsProfile(profile As SettingsProfile, confirmReplace As Func(Of Boolean)) As Boolean
        profile.Validate()
        Dim replacement = profile.Settings.ApplyTo(CurrentSettings())
        If HasUnsavedSettings() AndAlso System.Text.Json.JsonSerializer.Serialize(replacement) <> System.Text.Json.JsonSerializer.Serialize(CurrentSettings()) AndAlso Not confirmReplace() Then Return False
        settings = replacement : ApplySettings()
        InvalidateHeadset() : RefreshState()
        notice.Text = "Loaded profile: " & profile.Name & ". Use Save settings or Launch to keep these choices."
        Return True
    End Function

    Private Sub RefreshState()
        If IsDisposed Then Return
        Dim running = InstallContext.GameRunning()
        Dim pending = New OwnedFileTransaction(context).Pending
        Dim reason = RuntimeCapabilities.ExperimentalReason(context, CurrentSettings())
        state.Text = If(busy, "Working…", If(interactive IsNot Nothing, InteractiveSession.OutputStatus(interactive.ReadNativeTail()), If(launchProgress IsNot Nothing, "Starting DiRT 3…", If(pending, "Recovery pending — close DiRT 3, then restore original files.", If(running, "DiRT 3 is running.", If(reason = "", "Ready · experimental VDXR enabled", reason))))))
        If Not busy AndAlso desktopStartup IsNot Nothing Then state.Text = "DiRT 3 startup session · original startup files will be restored after exit."
        desktop.Text = If(running AndAlso launchProgress Is Nothing, "Show game", "Launch")
        desktop.Enabled = Not busy AndAlso launchProgress Is Nothing AndAlso (running OrElse Not pending)
        vr.Text = If(interactive IsNot Nothing, "Stop VR / quit", "Launch VR")
        vr.Enabled = Not busy AndAlso (interactive IsNot Nothing OrElse (Not running AndAlso Not pending AndAlso reason = ""))
        tips.SetToolTip(vr, If(reason = "", "Start a temporary experimental VDXR session.", reason))
        save.Enabled = Not busy : recover.Enabled = Not busy AndAlso Not running
        tabs.Enabled = Not busy AndAlso interactive Is Nothing AndAlso desktopStartup Is Nothing
        probe.Enabled = Not busy AndAlso Not running
        refreshHeadset.Enabled = Not busy AndAlso Not running
    End Sub

    Private Async Sub ObserveInteractive()
        Dim owned = interactive
        Try
            notice.Text = Await owned.WaitAndRestoreAsync()
        Catch ex As Exception
            notice.Text = "DiRT 3 closed. Recovery is pending: " & ex.Message
        Finally
            launchProgress?.Dispose() : launchProgress = Nothing
            interactive = Nothing
            RefreshState() : LoadDiagnostics()
        End Try
    End Sub

    Private Async Sub ObserveDesktopStartup()
        Dim owned = desktopStartup
        Dim message As String
        Try
            message = Await owned.WaitAndRestoreAsync()
        Catch ex As Exception
            message = "DiRT 3 closed. Startup file recovery is pending: " & ex.Message
        Finally
            launchProgress?.Dispose() : launchProgress = Nothing
            desktopStartup = Nothing
        End Try
        If Not IsDisposed Then
            notice.Text = message
            RefreshState() : LoadDiagnostics()
        End If
    End Sub

    Protected Overrides Sub OnFormClosing(e As FormClosingEventArgs)
        If interactive IsNot Nothing OrElse desktopStartup IsNot Nothing OrElse busy Then
            e.Cancel = True
            WindowState = FormWindowState.Minimized
            notice.Text = "The launcher stays open until this operation and file recovery finish."
            Return
        End If
        MyBase.OnFormClosing(e)
        If Not e.Cancel AndAlso windowReady Then
            Dim normal = If(WindowState = FormWindowState.Normal, Size, RestoreBounds.Size)
            Dim preferences = WindowPreferences.Capture(normal, DeviceDpi, lastWindowState = FormWindowState.Maximized)
            preferences.CollapsedSections = collapsedSectionTitles
            preferences.Save(context)
        End If
    End Sub

    Private Sub LoadDiagnostics()
        Try
            If Not File.Exists(context.DiagnosticsPath) Then
                diagnostics.Text = "No report yet." & Environment.NewLine & context.DiagnosticsPath
            ElseIf New FileInfo(context.DiagnosticsPath).Length > 1024 * 1024 Then
                diagnostics.Text = "The report is too large to display. Open logs to inspect it."
            Else
                SafeFiles.NoLinks(context.DiagnosticsPath)
                diagnostics.Text = File.ReadAllText(context.DiagnosticsPath)
            End If
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            diagnostics.Text = "Could not read the diagnostic report: " & ex.Message
        End Try
    End Sub

    Private Async Function ActionAsync(action As Func(Of Task)) As Task
        If busy Then Return
        busy = True : RefreshState()
        Try
            Await action()
        Catch ex As Exception
            If Not IsDisposed Then
                notice.Text = ex.Message
                probeStatus.Text = If(probeStatus.Text.StartsWith("Checking", StringComparison.Ordinal), "Runtime check did not finish: " & ex.Message, probeStatus.Text)
                LoadDiagnostics()
            End If
        Finally
            busy = False : RefreshState()
        End Try
    End Function

    Private Sub SafeAction(action As Action)
        Try
            action()
        Catch ex As Exception
            notice.Text = ex.Message
        End Try
    End Sub

    Private Shared Function Info(text As String, Optional bold As Boolean = False) As Label
        Dim label As New Label With {.Text = text, .AutoSize = True, .UseMnemonic = False, .Margin = New Padding(0, 4, 0, 12)}
        If bold Then label.Font = New Font("Segoe UI", 11, FontStyle.Bold)
        Return label
    End Function

    Private Shared Sub RefreshLabelWidths(parent As Control)
        For Each child As Control In parent.Controls
            If TypeOf child Is Label AndAlso child.AutoSize Then child.MaximumSize = New Size(Math.Max(1, parent.ClientSize.Width - parent.Padding.Horizontal - child.Margin.Horizontal), 0)
            RefreshLabelWidths(child)
        Next
    End Sub

    Private Sub ShowHelp()
        Using form As New LauncherForm With {.Text = "DiRT3VR — About and instructions", .Font = Font, .StartPosition = FormStartPosition.CenterParent, .Size = New Size(650, 500), .MinimumSize = New Size(500, 350)}
            Dim text As New TextBox With {.ReadOnly = True, .Multiline = True, .ScrollBars = ScrollBars.Vertical, .Dock = DockStyle.Fill, .BorderStyle = BorderStyle.None}
            text.Text = "DiRT3VR " & BuildVersion & " — Research build" & vbCrLf & vbCrLf &
                "Launch starts DiRT 3 Complete Edition with the desktop choices under Graphics. Use game setting preserves each existing option. Desktop changes remain in the game graphics file; exact backups are kept in desktop-backups under the preferences folder below. Save settings only saves launcher preferences. Steam must be running with access to the game. Driving controls and saves remain managed by DiRT 3." & vbCrLf & vbCrLf &
                "Advanced → Enable experimental VR enables the VDXR session when supported by this package. All rendered 3D camera views use stereo and tracking. Loading screens and movies use a virtual screen. Camera transitions remain experimental. " & RecenterShortcut.Describe(recenterKey, recenterModifiers) & " recenters while the game is foreground; change the key under Controls." & vbCrLf & vbCrLf &
                "Settings → Check runtime and headset runs an x86 OpenXR probe without starting the game. Choose VDXR or SteamVR explicitly; the selected runtime is used for that process only, independently of the system OpenXR default. VDXR and SteamVR require separate checks. Browse to the selected runtime file if it is installed elsewhere." & vbCrLf & vbCrLf &
                "Settings → Settings profiles keeps named presets of desktop, VR, startup, appearance and logging choices. Save as new profile and Overwrite profile store the current controls in a profile without saving the launcher's settings. Save settings (or Launch) keeps the current controls as the launcher's settings and never changes a profile. Load copies a profile into the controls and asks before replacing unsaved edits. Import adds a profile without loading it, and Export writes the selected saved profile. Game saves and local paths are excluded." & vbCrLf & vbCrLf &
                "Launcher → Skip startup logo movies temporarily replaces the two startup movie states for desktop or VR. Copyright and Press Start remain. Keep the launcher open until game exit for restoration; closing it during the session minimizes it. Leave skipping off for multiplayer until checked. Background play is not implemented for DiRT 3." & vbCrLf & vbCrLf &
                ExperimentalFiles.ProfileDescription & vbCrLf & vbCrLf &
                "Restore original files only handles this launcher's recorded changes. Foreign mods and real settings changes are preserved. Keep the launcher open until the game exits and recovery finishes." & vbCrLf & vbCrLf &
                "Preferences and diagnostics: " & context.UserRoot & vbCrLf & vbCrLf &
                "The native WinForms layout and appearance are adapted from DiRT2VR, with yellow DiRT3VR accents. Modern / Classic follows Windows light, dark and high-contrast preferences."
            form.Padding = New Padding(20) : form.Controls.Add(text) : form.ShowDialog(Me)
        End Using
    End Sub

    Protected Overrides Sub Dispose(disposing As Boolean)
        If disposing AndAlso Not resourcesDisposed Then
            resourcesDisposed = True
            lifetime.Cancel() : lifetime.Dispose()
            timer.Dispose() : tips.Dispose() : launchProgress?.Dispose()
        End If
        MyBase.Dispose(disposing)
    End Sub
End Class
