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
    Private ReadOnly updateNotice As New Button With {.Name = "UpdateAvailable", .AutoSize = True, .Visible = False, .Margin = New Padding(0, 4, 8, 0)}
    Private ReadOnly layerSummary As New Label With {.Name = "LayerSummary", .AutoSize = True, .UseMnemonic = False, .Margin = New Padding(0, 4, 0, 12)}
    Private availableUpdate As ReleaseUpdate
    Private ReadOnly recenterHint As New Label With {.Name = "RecenterHint", .AutoSize = True, .UseMnemonic = False, .Margin = New Padding(0, 4, 0, 12)}
    Private recenterKey As Integer = RecenterShortcut.DefaultKey, recenterModifiers As Integer
    Private capturingRecenter As Boolean
    Private ReadOnly recenterDeviceButton As New Button With {.Name = "RecenterDevice", .AutoSize = True}
    Private ReadOnly clearRecenterDevice As New Button With {.Name = "ClearRecenterDevice", .Text = "Clear", .AutoSize = True}
    Private recenterDevice As RecenterDeviceBinding
    Private recenterWatcher As RecenterDeviceWatcher
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
    Private ReadOnly stopVrButton As New Button With {.Name = "StopVR", .Text = "Stop VR", .AutoSize = True, .Visible = False}
    Private ReadOnly save As New Button With {.Name = "SaveSettings", .Text = "Save settings", .AutoSize = True}
    Private ReadOnly recover As New Button With {.Name = "RestoreFiles", .Text = "Restore original files", .AutoSize = True}
    Private ReadOnly logs As New Button With {.Name = "OpenLogs", .Text = "Open logs", .AutoSize = True}
    Private ReadOnly tips As New ToolTip With {.AutoPopDelay = 20000}
    Private ReadOnly timer As New Timer With {.Interval = 1500}
    Private ReadOnly background As BackgroundHost
    Private ReadOnly exitTimer As New Timer With {.Interval = 6000}
    Private actionFailed As Boolean
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
        background = New BackgroundHost(Me, AddressOf ShowFromBackground, AddressOf StopVrKeepPlaying, AddressOf EndSession,
                                        Function() If(interactive Is Nothing, CType(Nothing, Boolean?), Not interactive.VrStopped))
        AddHandler exitTimer.Tick, Sub()
                                       exitTimer.Stop()
                                       If background.Active AndAlso Not SessionActive() Then Close()
                                   End Sub
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
        AddHandler updateNotice.Click, Sub() ShowHelp()
        Dim headerActions As New FlowLayoutPanel With {.AutoSize = True, .WrapContents = False, .Margin = New Padding(0), .Anchor = AnchorStyles.Top Or AnchorStyles.Right}
        headerActions.Controls.AddRange({updateNotice, help})
        header.Controls.Add(headerActions, 1, 0)
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
        layout.Controls.Add(New LauncherFooter(desktop, vr, save, recover, logs, stopVrButton), 0, 4)
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
                                                                       notice.Text = If(desktopStartup Is Nothing, "Starting DiRT 3… Closing this launcher leaves the game running.", "Starting DiRT 3… The original startup file is restored after exit, even if you close this window.")
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
                                                              interactive = Await InteractiveSession.StartAsync(context, selected, AddressOf ConfirmLayerBypass)
                                                              StartRecenterWatcher(selected.RecenterDevice)
                                                              UpdateLayerSummary()
                                                              settings = selected
                                                              RememberSavedSettings()
                                                              launchProgress = New LaunchProgressMonitor(Me, interactive.Game, context, vr:=True)
                                                              AddHandler launchProgress.Completed, Sub(message As String)
                                                                                                        launchProgress = Nothing
                                                                                                        notice.Text = message
                                                                                                        ' Once DiRT 3's window exists, say plainly if it loaded Windows' d3d11.dll instead of DiRT3VR's.
                                                                                                        Dim session = interactive
                                                                                                        If session IsNot Nothing AndAlso Not session.Game.HasExited Then
                                                                                                            Dim renderer = RendererLoad.Diagnose(context, session.Game.Id, session.LogDirectory)
                                                                                                            If renderer.Message IsNot Nothing Then notice.Text = renderer.Message
                                                                                                        End If
                                                                                                        RefreshState()
                                                                                                    End Sub
                                                              notice.Text = "Starting experimental VR. Original files are restored when DiRT 3 closes, even if you close this window."
                                                              launchProgress.Start()
                                                              ObserveInteractive()
                                                          End Function)
        AddHandler stopVrButton.Click, Sub() StopVrKeepPlaying()
        AddHandler recover.Click, Async Sub() Await ActionAsync(Async Function()
                                                                   Using lease = InteractiveSession.AcquireLease(context)
                                                                       notice.Text = Await RestoreOriginalFilesAsync()
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
        AddHandler Shown, Async Sub() Await StartupUpdateCheckAsync()
        tips.SetToolTip(desktop, "Start the game with your desktop graphics choices and saved driving controls. No headset is required.")
        tips.SetToolTip(stopVrButton, "Turn VR off and keep playing DiRT 3 on the monitor. Original files are restored when DiRT 3 closes.")
        tips.SetToolTip(save, "Keep the current controls as the launcher's settings for next time, as Launch does. Settings profiles are not changed.")
        tips.SetToolTip(probe, "Check OpenXR and headset access without starting DiRT 3. This does not validate stereoscopic game rendering.")
        tips.SetToolTip(skipStartupMovies, "Skips the AMD and DiRT 3 logo movies in desktop and VR launches. Copyright and Press Start remain. The original startup file is restored when the game exits; closing this window keeps DiRT3VR running in the notification area until then.")
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
        startup.Controls.Add(Info("Applies to Launch and Launch VR. Copyright and Press Start remain. DiRT3VR restores the original startup file when the game closes, also when you close this window (it stays in the notification area until then). Leave this option off for multiplayer until compatibility is checked."))
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
                                  Dim current = CurrentSettings()
                                  Dim preflight = Await VrLayerPreflight.ReadHeadsetAsync(context, Function(layers) VrRuntime.ReadHeadsetAsync(context, current, lifetime.Token, layers:=layers), AddressOf ConfirmLayerBypass)
                                  headsetResolution = preflight.Headset
                                  If preflight.Warning <> "" Then notice.Text = preflight.Warning
                                  UpdateLayerSummary()
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
        general.Controls.Add(Info("Use your existing keyboard, controller or wheel bindings in DiRT 3. During experimental VR, closing this window keeps DiRT3VR running in the notification area until the game exits and original files are restored."))
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
        Dim deviceRow As New FlowLayoutPanel With {.AutoSize = True, .WrapContents = True, .Margin = New Padding(0)}
        deviceRow.Controls.AddRange({recenterDeviceButton, clearRecenterDevice})
        Field(vrControls, "Controller / wheel button", deviceRow)
        vrControls.Controls.Add(Info("Optional: a controller, wheel or button-box button (or D-pad / hat direction) that also recenters while DiRT 3 is in front. The launcher reads the device without taking it over and sends nothing to the game."))
        AddHandler recenterDeviceButton.Click, Sub()
                                                   Using dialog As New RecenterDeviceForm(DrivingInput.LibraryPath(context))
                                                       If dialog.ShowDialog(Me) = DialogResult.OK AndAlso dialog.Binding IsNot Nothing Then SetRecenterDevice(dialog.Binding)
                                                   End Using
                                               End Sub
        AddHandler clearRecenterDevice.Click, Sub() SetRecenterDevice(Nothing)
        UpdateRecenterDeviceLabel()
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

    ' Public for the launcher tests; the dialog sets it after a button press.
    Public Sub SetRecenterDevice(value As RecenterDeviceBinding)
        value?.Validate()
        recenterDevice = value
        UpdateRecenterDeviceLabel()
        notice.Text = If(value Is Nothing, "Controller / wheel recenter cleared.", "Recenter button set to " & value.Describe() & ".") & " Save settings or Launch to keep it."
    End Sub

    Private Sub UpdateRecenterDeviceLabel()
        recenterDeviceButton.Text = If(recenterDevice Is Nothing, "Bind a button…", recenterDevice.Describe())
        clearRecenterDevice.Enabled = recenterDevice IsNot Nothing
    End Sub

    ' Watches the bound controller/wheel button while this VR session runs.
    Private Sub StartRecenterWatcher(binding As RecenterDeviceBinding)
        recenterWatcher?.Dispose() : recenterWatcher = Nothing
        If binding Is Nothing OrElse interactive Is Nothing Then Return
        Try
            recenterWatcher = RecenterDeviceWatcher.ForSession(DrivingInput.LibraryPath(context), Handle, binding, interactive.Game.Id, interactive.RecenterRequestPath)
            recenterWatcher.Start()
        Catch ex As IOException
            notice.Text &= " Controller recenter is unavailable: " & ex.Message
        End Try
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
        Dim addOns As New Button With {.Name = "VrCompatibility", .Text = "OpenXR add-ons…", .AutoSize = True}
        runtimes.Controls.Add(addOns) : runtimes.Controls.Add(layerSummary)
        AddHandler addOns.Click, Sub()
                                     Using dialog As New VrCompatibilityForm(context)
                                         dialog.ShowDialog(Me)
                                     End Using
                                     UpdateLayerSummary()
                                 End Sub
        UpdateLayerSummary()
        Dim options = Section(content, "Diagnostics")
        options.Controls.Add(Info("Startup results and failures are always recorded in Open logs."))
    End Sub

    Private Sub BuildAdvanced()
        Dim content = Page("Advanced")
        Dim capability = Section(content, "Implementation status")
        capability.Controls.Add(experimental)
        capability.Controls.Add(Info("Opt in to the experimental VDXR session. Version 0.1.5 has user-confirmed interactive VR with cockpit and external views. Full camera, effects and sustained-driving validation is still in progress. A runtime check alone does not establish game support."))
        capability.Controls.Add(Info("Stop VR turns the headset off and keeps DiRT 3 running on the monitor. End session, or quitting in the game, closes DiRT 3; original files are restored after it closes. You can close this window meanwhile: DiRT3VR stays in the notification area and quits once the files are back."))
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
        recenterDevice = settings.RecenterDevice : UpdateRecenterDeviceLabel()
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
            .RecenterKey = recenterKey, .RecenterModifiers = recenterModifiers, .RecenterDevice = recenterDevice}
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
        If Not busy AndAlso interactive IsNot Nothing AndAlso interactive.VrStopped Then state.Text = "VR stopped · DiRT 3 runs on the monitor · original files will be restored after exit."
        If background.Active Then background.UpdateStatus(state.Text)
        desktop.Text = If(running AndAlso launchProgress Is Nothing, "Show game", "Launch")
        desktop.Enabled = Not busy AndAlso launchProgress Is Nothing AndAlso (running OrElse Not pending)
        vr.Text = If(interactive IsNot Nothing, "End session", "Launch VR")
        vr.Enabled = Not busy AndAlso (interactive IsNot Nothing OrElse (Not running AndAlso Not pending AndAlso reason = ""))
        tips.SetToolTip(vr, If(interactive IsNot Nothing, "Close DiRT 3 and restore the original files.", If(reason = "", "Start a temporary experimental VDXR session.", reason)))
        stopVrButton.Visible = interactive IsNot Nothing
        stopVrButton.Enabled = Not busy AndAlso interactive IsNot Nothing AndAlso Not interactive.VrStopped
        save.Enabled = Not busy : recover.Enabled = Not busy AndAlso Not running
        tabs.Enabled = Not busy AndAlso interactive Is Nothing AndAlso desktopStartup Is Nothing
        probe.Enabled = Not busy AndAlso Not running
        refreshHeadset.Enabled = Not busy AndAlso Not running
    End Sub

    Private Async Sub ObserveInteractive()
        Dim owned = interactive
        Dim failed = False
        Try
            notice.Text = Await owned.WaitAndRestoreAsync()
        Catch ex As Exception
            notice.Text = "DiRT 3 closed. Recovery is pending: " & ex.Message
            failed = True
        Finally
            recenterWatcher?.Dispose() : recenterWatcher = Nothing
            launchProgress?.Dispose() : launchProgress = Nothing
            interactive = Nothing
            RefreshState() : LoadDiagnostics()
        End Try
        FinishBackground(failed)
    End Sub

    Private Async Sub ObserveDesktopStartup()
        Dim owned = desktopStartup
        Dim message As String
        Dim failed = False
        Try
            message = Await owned.WaitAndRestoreAsync()
        Catch ex As Exception
            message = "DiRT 3 closed. Startup file recovery is pending: " & ex.Message
            failed = True
        Finally
            launchProgress?.Dispose() : launchProgress = Nothing
            desktopStartup = Nothing
        End Try
        If Not IsDisposed Then
            notice.Text = message
            RefreshState() : LoadDiagnostics()
            FinishBackground(failed)
        End If
    End Sub

    ' A game session with temporary file changes, or a file operation, is in progress.
    Private Function SessionActive() As Boolean
        Return interactive IsNot Nothing OrElse desktopStartup IsNot Nothing OrElse busy
    End Function

    Protected Overrides Sub OnFormClosing(e As FormClosingEventArgs)
        If BackgroundHost.KeepsRunningOnClose(SessionActive(), e.CloseReason) Then
            ' DiRT3VR keeps the session and restores the files after the game closes, without its window.
            e.Cancel = True
            SaveWindowPreferences()
            Dim explanation = If(interactive IsNot Nothing OrElse desktopStartup IsNot Nothing,
                                 "DiRT3VR restores the original game files when DiRT 3 closes, then quits.",
                                 "DiRT3VR finishes the current task, then quits.")
            background.Enter(state.Text, explanation)
            Return
        End If
        MyBase.OnFormClosing(e)
        If Not e.Cancel Then SaveWindowPreferences()
    End Sub

    Private Sub SaveWindowPreferences()
        If Not windowReady Then Return
        Dim normal = If(WindowState = FormWindowState.Normal, Size, RestoreBounds.Size)
        Dim preferences = WindowPreferences.Capture(normal, DeviceDpi, lastWindowState = FormWindowState.Maximized)
        preferences.CollapsedSections = collapsedSectionTitles
        preferences.Save(context)
    End Sub

    ' Brings the window back from the notification area, or to the front (also when DiRT3VR is
    ' started again from the Start menu). Public for the second launcher's signal and the tests.
    Public Sub ShowFromBackground()
        If IsDisposed Then Return
        exitTimer.Stop()
        If background.Active Then
            background.Leave()
        Else
            Show()
            If WindowState = FormWindowState.Minimized Then WindowState = If(lastWindowState = FormWindowState.Maximized, FormWindowState.Maximized, FormWindowState.Normal)
            Activate()
        End If
    End Sub

    ' The session ended while the window was hidden: quit once the files are back, or show the problem.
    Private Sub FinishBackground(failed As Boolean)
        If IsDisposed OrElse Not background.Active OrElse SessionActive() Then Return
        Dim pending = True
        Try
            pending = New OwnedFileTransaction(context).Pending
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            ' Unknown: show the window.
        End Try
        If BackgroundHost.QuitsAfterSession(pending, failed) Then
            background.Finish(notice.Text)
            exitTimer.Start()
        Else
            background.Leave()
        End If
    End Sub

    ' Asks what to keep when DiRT 3's graphics settings changed during the session beyond the VR
    ' values (public so the launcher tests can answer it).
    <System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)>
    Public Property GraphicsChoicePrompt As Func(Of GraphicsConflictKind, GraphicsRecoveryChoice) = AddressOf AskGraphicsChoice

    ' Restore original files: restores what it can, then lets the user decide about changed graphics settings.
    Private Async Function RestoreOriginalFilesAsync() As Task(Of String)
        Dim failure As IOException
        Try
            Return If(Await RecoveryWorker.RecoverAsync(context), "Owned session files were restored.", "No launcher file recovery is pending. No game files were changed.")
        Catch ex As IOException
            failure = ex
        End Try
        Dim kind = New OwnedFileTransaction(context).GraphicsConflict()
        If kind = GraphicsConflictKind.None Then Throw failure
        Dim choice = GraphicsChoicePrompt(kind)
        If choice = GraphicsRecoveryChoice.Ask Then Return failure.Message
        Await RecoveryWorker.RecoverAsync(context, choice)
        Return If(choice = GraphicsRecoveryChoice.KeepGameChanges,
                  "Original files restored. DiRT 3's graphics settings keep the changes made during the session, with the display values from before VR.",
                  "Original files restored, including DiRT 3's graphics settings from before the session. The changed version was kept in the recovery folder.")
    End Function

    Private Function AskGraphicsChoice(kind As GraphicsConflictKind) As GraphicsRecoveryChoice
        If background.Active Then background.Leave()
        Dim keep As New TaskDialogCommandLinkButton("Keep the changes made in DiRT 3", "Puts back the resolution, window mode, refresh rate, VSync, anti-aliasing and DirectX settings from before VR. Other graphics settings changed during the session stay.") With {.Enabled = kind = GraphicsConflictKind.Mergeable}
        Dim restore As New TaskDialogCommandLinkButton("Restore all graphics settings from before the session", "DiRT 3's current file is kept as a copy in the recovery folder.")
        Dim later As New TaskDialogCommandLinkButton("Decide later", "The graphics settings stay as they are for now. The other game files are already restored.")
        Dim page As New TaskDialogPage With {
            .Caption = "DiRT3VR", .Icon = TaskDialogIcon.Warning,
            .Heading = "DiRT 3's graphics settings changed during the session",
            .Text = If(kind = GraphicsConflictKind.Mergeable,
                       "Besides the temporary VR display values, other graphics settings changed while DiRT 3 ran (for example in its options menu).",
                       "The graphics settings file is no longer valid settings XML, so only the settings from before the session can be restored."),
            .Buttons = New TaskDialogButtonCollection From {keep, restore, later}}
        Dim result = TaskDialog.ShowDialog(Me, page)
        If result Is keep Then Return GraphicsRecoveryChoice.KeepGameChanges
        If result Is restore Then Return GraphicsRecoveryChoice.RestoreOriginal
        Return GraphicsRecoveryChoice.Ask
    End Function

    ' Stop VR: the headset turns off and DiRT 3 keeps running on the monitor until it is closed.
    Private Sub StopVrKeepPlaying()
        If interactive Is Nothing OrElse busy Then Return
        SafeAction(Sub() notice.Text = interactive.StopVr())
        RefreshState()
    End Sub

    ' End session from the notification area: close DiRT 3; the files are restored afterwards.
    Private Async Sub EndSession()
        If busy Then Return
        If interactive IsNot Nothing Then
            Dim owned = interactive
            Await ActionAsync(Async Function()
                                  notice.Text = Await owned.StopAsync()
                              End Function)
        ElseIf desktopStartup IsNot Nothing Then
            SafeAction(Sub() notice.Text = desktopStartup.RequestExit())
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
        busy = True : actionFailed = False : RefreshState()
        Try
            Await action()
        Catch ex As Exception
            actionFailed = True
            If Not IsDisposed Then
                notice.Text = ex.Message
                probeStatus.Text = If(probeStatus.Text.StartsWith("Checking", StringComparison.Ordinal), "Runtime check did not finish: " & ex.Message, probeStatus.Text)
                LoadDiagnostics()
            End If
        Finally
            busy = False : RefreshState()
        End Try
        ' Closed while working (for example during Launch VR): continue, show the error, or quit.
        FinishBackground(actionFailed)
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

    Private Function HelpText() As String
        Return "DiRT3VR " & BuildVersion & " — Research build" & vbCrLf & vbCrLf &
            "Launch starts DiRT 3 Complete Edition with the desktop choices under Graphics. Use game setting preserves each existing option. Desktop changes remain in the game graphics file; exact backups are kept in desktop-backups under the preferences folder below. Save settings only saves launcher preferences. Steam must be running with access to the game. Driving controls and saves remain managed by DiRT 3." & vbCrLf & vbCrLf &
            "Advanced → Enable experimental VR enables the VDXR session when supported by this package. All rendered 3D camera views use stereo and tracking. Loading screens and movies use a virtual screen. Camera transitions remain experimental. " & RecenterShortcut.Describe(recenterKey, recenterModifiers) & " recenters while the game is foreground; change the key under Controls." & vbCrLf & vbCrLf &
            "Settings → Check runtime and headset runs an x86 OpenXR probe without starting the game. Choose VDXR or SteamVR explicitly; the selected runtime is used for that process only, independently of the system OpenXR default. VDXR and SteamVR require separate checks. Browse to the selected runtime file if it is installed elsewhere." & vbCrLf & vbCrLf &
            "Settings → Settings profiles keeps named presets of desktop, VR, startup, appearance and logging choices. Save as new profile and Overwrite profile store the current controls in a profile without saving the launcher's settings. Save settings (or Launch) keeps the current controls as the launcher's settings and never changes a profile. Load copies a profile into the controls and asks before replacing unsaved edits. Import adds a profile without loading it, and Export writes the selected saved profile. Game saves and local paths are excluded." & vbCrLf & vbCrLf &
            "Launcher → Skip startup logo movies temporarily replaces the two startup movie states for desktop or VR. Copyright and Press Start remain. The original startup file is restored when the game exits. Leave skipping off for multiplayer until checked." & vbCrLf & vbCrLf &
            "During a session, Stop VR turns the headset off and keeps DiRT 3 running on the monitor; End session closes DiRT 3. Closing this window keeps DiRT3VR in the notification area (next to the clock) until DiRT 3 closes and the original files are restored; it then quits by itself. Right-click its icon to open the window, stop VR or end the session. Starting DiRT3VR again opens the running window." & vbCrLf & vbCrLf &
            ExperimentalFiles.ProfileDescription & vbCrLf & vbCrLf &
            "Restore original files only handles this launcher's recorded changes. Foreign mods and real settings changes are preserved, and the other files are still restored. If DiRT 3's graphics settings changed during the session beyond the VR values, it asks whether to keep those changes or restore the settings from before the session. If Windows shuts down during a session, use it on the next start." & vbCrLf & vbCrLf &
            "Preferences and diagnostics: " & context.UserRoot & vbCrLf & vbCrLf &
            "The native WinForms layout and appearance are adapted from DiRT2VR, with yellow DiRT3VR accents. Modern / Classic follows Windows light, dark and high-contrast preferences."
    End Function

    Private Sub ShowHelp()
        Dim changed = False
        Using form As New AboutForm(context, HelpText(), availableUpdate, blocker:=AddressOf UpdateBlocker) With {.Font = Font}
            AddHandler form.UpdatePreferenceChanged, Sub()
                                                         changed = True : availableUpdate = Nothing : updateNotice.Visible = False
                                                     End Sub
            form.ShowDialog(Me)
        End Using
        If changed AndAlso Not IsDisposed Then
            Dim recheck = StartupUpdateCheckAsync()
        End If
    End Sub

    ' Asks before turning an OpenXR add-on off for DiRT 3 when it stops VR from starting.
    Private Function ConfirmLayerBypass(request As VrLayerRecoveryRequest) As VrLayerRecoveryChoice
        If background.Active Then background.Leave() ' The question needs the window, also when it was closed during Launch VR.
        Return VrLayerRecoveryForm.Confirm(request, Me)
    End Function

    Private Sub UpdateLayerSummary()
        Dim names = VrLayerPreferences.Load(context).Bypasses.Select(Function(layer) layer.DisplayName).ToArray()
        layerSummary.Text = If(names.Length = 0, "OpenXR add-ons (such as ReShade or the OBS mirror) that stop VR from starting can be turned off for DiRT 3 when you launch VR. None are turned off now.",
                               "Turned off for DiRT 3: " & String.Join(", ", names) & ". Other apps still use them.")
    End Sub

    ' Setup needs DiRT 3 closed and the original files back.
    Private Function UpdateBlocker() As String
        If interactive IsNot Nothing OrElse desktopStartup IsNot Nothing OrElse launchProgress IsNot Nothing OrElse busy Then Return "Close DiRT 3 and wait until the launcher has restored the original files, then update."
        Return ""
    End Function

    ' Installed packages check GitHub Releases once at startup; development and test builds don't.
    Private Async Function StartupUpdateCheckAsync() As Task
        If Not UpdateService.IsInstalledPackage(context) Then Return
        Await CheckForUpdateAsync(AddressOf CheckReleaseAsync)
    End Function

    Private Async Function CheckReleaseAsync(token As Threading.CancellationToken) As Task(Of ReleaseUpdate)
        Using client = UpdateService.CreateClient()
            Return Await New UpdateService(client).CheckAsync(BuildInfo.Version, UpdatePreferences.Load(context).IncludeExperimentalReleases, token)
        End Using
    End Function

    ' Quiet: a failed check, or no public releases yet, shows nothing (public for the launcher tests).
    Public Async Function CheckForUpdateAsync(check As Func(Of Threading.CancellationToken, Task(Of ReleaseUpdate))) As Task
        Dim result As ReleaseUpdate
        Try
            result = Await check(lifetime.Token)
        Catch ex As Exception
            Return
        End Try
        If IsDisposed OrElse result Is Nothing Then Return
        If result.IsExperimental AndAlso Not UpdatePreferences.Load(context).IncludeExperimentalReleases Then Return
        availableUpdate = result
        updateNotice.Text = "Update available: " & result.Version.Text
        updateNotice.Visible = True
        tips.SetToolTip(updateNotice, result.Describe() & " is available. Click to download and install it.")
    End Function

    Protected Overrides Sub Dispose(disposing As Boolean)
        If disposing AndAlso Not resourcesDisposed Then
            resourcesDisposed = True
            lifetime.Cancel() : lifetime.Dispose()
            timer.Dispose() : tips.Dispose() : launchProgress?.Dispose() : recenterWatcher?.Dispose()
            exitTimer.Dispose() : background.Dispose()
        End If
        MyBase.Dispose(disposing)
    End Sub
End Class
