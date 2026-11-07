Imports System.Text

' The UI owns this object until the child exits and recovery finishes.
Public Class InteractiveSession
    Private ReadOnly context As InstallContext
    Private ReadOnly lease As FileStream
    Public ReadOnly Game As Process
    Public ReadOnly LogDirectory As String
    Public ReadOnly StopPath As String
    Public ReadOnly RecenterRequestPath As String
    ' Where the Toggle VR button's requests appear (#288ceec1); the renderer deletes the file when it acts.
    Public ReadOnly ToggleRequestPath As String
    Public ReadOnly StartedUtc As DateTime
    ' Cars whose seat position couldn't be applied (their camera file is missing or not the expected format).
    Public Property SeatsSkipped As IReadOnlyList(Of String) = Array.Empty(Of String)()
    ' Why motion blur stayed on for this session, or "" when it was switched off (or already off).
    Public Property MotionBlurNote As String = ""
    ' Devices whose launcher driving bindings couldn't go into the session's action maps, or "".
    Public Property DrivingNote As String = ""
    ' Why SteamVR's desktop theater stayed on, or "" (#24867b69).
    Public Property TheaterNote As String = ""
    ' The chosen VR render scale didn't fit this headset; the launch message says what ran.
    Public Property ScaleNote As String = ""
    ' The SteamVR folder whose theater setting this session turned off, or Nothing.
    Private steamVrFolder As String
    ' The in-game seat menu's shared memory for this session (#bd820b32), or Nothing without a menu key or button.
    Public Property SeatMenu As SeatChannel
    ' A seat position saved from the in-game menu just before DiRT 3 closed, for the window to show.
    Public Property FinalSeatCommit As SeatCommit
    Private stopRequested As Boolean

    Private Sub New(install As InstallContext, child As Process, folder As String, sessionLease As FileStream)
        context = install : Game = child : LogDirectory = folder : lease = sessionLease
        StopPath = Path.Combine(folder, "xr-stop.request")
        RecenterRequestPath = Path.Combine(folder, "recenter.request")
        ToggleRequestPath = Path.Combine(folder, "toggle-vr.request")
        StartedUtc = DateTime.UtcNow
        Try
            StartedUtc = child.StartTime.ToUniversalTime()
        Catch ex As Exception When TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is ComponentModel.Win32Exception
            ' Preserve the process handle even if the child exits immediately.
        End Try
    End Sub

    Public Shared Function StartInfo(context As InstallContext, runtime As RuntimeInfo, directory As String, settings As LauncherSettings, headset As HeadsetResolution, Optional layers As IReadOnlyList(Of VrLayerBypass) = Nothing, Optional seatChannelName As String = Nothing, Optional directDevices As IReadOnlyList(Of String) = Nothing) As ProcessStartInfo
        If Not Path.IsPathFullyQualified(directory) Then Throw New IOException("The session log path must be absolute.")
        settings.Validate()
        If headset Is Nothing OrElse headset.RuntimeMode <> runtime.Mode Then Throw New IOException("Query the selected headset before launching.")
        Dim scale = headset.EffectiveScale(settings.VrRenderScale)
        headset.CroppedSceneSize(scale, settings.VrFovWidth(), settings.VrFovHeight())
        Dim start = Session.DesktopStartInfo(context)
        start.Environment("DIRT3VR_DIAGNOSTICS") = "1"
        start.Environment("DIRT3VR_SCENE_DIAGNOSTICS") = "1"
        start.Environment("DIRT3VR_PRESENTATION_TRACE") = If(settings.DiagnosticLogging, "1", "0")
        start.Environment("DIRT3VR_XR_PROTOTYPE") = "1"
        start.Environment("DIRT3VR_XR_INTERACTIVE") = "1"
        start.Environment("DIRT3VR_VR_RENDER_SCALE") = scale.ToString(Globalization.CultureInfo.InvariantCulture)
        start.Environment("DIRT3VR_RECENTER_KEY") = String.Format(Globalization.CultureInfo.InvariantCulture, "{0} {1}", settings.RecenterKey, settings.RecenterModifiers)
        start.Environment("DIRT3VR_RECENTER_TILT") = If(settings.RecenterKeepLevel, "0", "1")
        ' The HUD panel's distance and follow mode (#3652b9aa, Lens's contract).
        start.Environment("DIRT3VR_HUD_DISTANCE") = settings.HudDistance.ToString("0.0", Globalization.CultureInfo.InvariantCulture)
        start.Environment("DIRT3VR_HUD_FOLLOW") = If(settings.HudFollowView, "1", "0")
        ' FPS and GPU usage in the headset (#4896ffd3, Lens's contract).
        start.Environment("DIRT3VR_PERF_DISPLAY") = If(settings.PerfDisplay, "1", "0")
        start.Environment("DIRT3VR_PERF_FOLLOW") = If(settings.PerfFollowView, "1", "0")
        ' Views on the flat screen (#48208327, Lens's contract): only the groups turned on; without it every view is 3D.
        Dim flatViews = settings.FlatViews()
        If flatViews <> "" Then start.Environment("DIRT3VR_FLAT_VIEWS") = flatViews Else start.Environment.Remove("DIRT3VR_FLAT_VIEWS")
        ' Anisotropic filtering (#15714dd2, Wrench's contract): only a choice other than the game's.
        Anisotropy.Configure(start, settings.VrAnisotropy)
        Session.ConfigureSteeringShake(start, settings)
        ' The VR field of view (#16a86c75, #47d16d40; Lens's contract): only a share under 100% is passed.
        For Each item In {("DIRT3VR_VR_FOV_WIDTH", settings.VrFovWidth()), ("DIRT3VR_VR_FOV_HEIGHT", settings.VrFovHeight())}
            If item.Item2 < 100 Then
                start.Environment(item.Item1) = item.Item2.ToString(Globalization.CultureInfo.InvariantCulture)
            Else
                start.Environment.Remove(item.Item1)
            End If
        Next
        ' Start in the cockpit (#cd56379b, Wrench's contract): the renderer keeps the race in the first view, which
        ' the session's camera files make the cockpit, instead of DiRT 3's switch to chase_close.
        If settings.StartInCockpit Then
            start.Environment("DIRT3VR_START_IN_COCKPIT") = "1"
        Else
            start.Environment.Remove("DIRT3VR_START_IN_COCKPIT")
        End If
        start.Environment("DIRT3VR_HEADSET_WIDTH") = headset.Width.ToString(Globalization.CultureInfo.InvariantCulture)
        start.Environment("DIRT3VR_HEADSET_HEIGHT") = headset.Height.ToString(Globalization.CultureInfo.InvariantCulture)
        start.Environment("DIRT3VR_LOG_DIR") = directory
        start.Environment("DIRT3VR_XR_STOP_FILE") = Path.Combine(directory, "xr-stop.request")
        ' A bound controller/wheel button asks for a recenter by creating this file; the renderer removes it.
        ' Without a binding the renderer gets no path, so it doesn't try to delete a file every frame.
        If settings.RecenterDevice IsNot Nothing Then
            start.Environment("DIRT3VR_RECENTER_REQUEST_FILE") = Path.Combine(directory, "recenter.request")
        Else
            start.Environment.Remove("DIRT3VR_RECENTER_REQUEST_FILE")
        End If
        ' Toggle VR (#288ceec1, Lens's contract): the key as "<vk> <modifiers>" and, with a bound button, its request file.
        If settings.ToggleVrKey <> 0 Then
            start.Environment("DIRT3VR_TOGGLE_KEY") = String.Format(Globalization.CultureInfo.InvariantCulture, "{0} {1}", settings.ToggleVrKey, settings.ToggleVrModifiers)
        Else
            start.Environment.Remove("DIRT3VR_TOGGLE_KEY")
        End If
        If settings.ToggleVrDevice IsNot Nothing Then
            start.Environment("DIRT3VR_TOGGLE_REQUEST_FILE") = Path.Combine(directory, "toggle-vr.request")
        Else
            start.Environment.Remove("DIRT3VR_TOGGLE_REQUEST_FILE")
        End If
        start.Environment("XR_RUNTIME_JSON") = runtime.ManifestPath
        ' The in-game seat menu's shared memory (docs/seat-channel.md); without it the renderer shows no menu.
        If String.IsNullOrEmpty(seatChannelName) Then
            start.Environment.Remove(SeatChannel.EnvironmentName)
        Else
            start.Environment(SeatChannel.EnvironmentName) = seatChannelName
        End If
        VrApiLayers.Configure(start, layers)
        If settings.LaunchMode = "direct" Then
            ' The renderer keeps the benchmark's car under the player's control (#a1d31909).
            DirectPractice.AddStart(start, settings, directDevices)
        End If
        Return start
    End Function

    Public Shared Function AcquireLease(context As InstallContext) As FileStream
        Dim path = IO.Path.Combine(IO.Path.GetDirectoryName(context.UserRoot), "interactive-session.lock")
        SafeFiles.NoLinks(path) : Directory.CreateDirectory(IO.Path.GetDirectoryName(path))
        Try
            Return New FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
        Catch ex As IOException
            Throw New IOException("Another DiRT3VR session is being prepared or is still running.", ex)
        End Try
    End Function

    Public Shared Async Function StartAsync(context As InstallContext, settings As LauncherSettings, Optional confirmLayers As Func(Of VrLayerRecoveryRequest, VrLayerRecoveryChoice) = Nothing) As Task(Of InteractiveSession)
        RuntimeCapabilities.RequireExperimental(context, settings)
        Dim lease = AcquireLease(context), game As Process = Nothing
        Dim transaction As New OwnedFileTransaction(context)
        Dim directory As String = Nothing, failure As Exception = Nothing, staged As Boolean = False
        Dim theaterNote As String = "", theaterFolder As String = Nothing
        Dim seatMenu As SeatChannel = Nothing
        Try
            context.RequireClosed() : context.ValidateGame()
            If transaction.Pending Then Throw New IOException("Restore pending session files before launching.")
            ' DiRT 3 inherits the launcher's image-load rule; refuse before staging anything if it would skip DiRT3VR's d3d11.dll.
            Dim blocked = RendererLoad.LaunchBlock(context)
            If blocked <> "" Then Throw New IOException(blocked)
            Dim runtime = VrRuntime.Resolve(settings.RuntimeMode, settings.SteamRuntimeManifest, settings.VdxrRuntimeManifest, settings.OtherRuntimeManifest)
            ' Always re-query the selected runtime before staging; UI results may be stale.
            ' An OpenXR add-on that blocks the probe can be turned off for DiRT 3 with the player's approval.
            Dim preflight = Await VrLayerPreflight.ReadHeadsetAsync(context, Function(layers) VrRuntime.ReadHeadsetAsync(context, settings, selectedRuntime:=runtime, layers:=layers), confirmLayers)
            Dim headset = preflight.Headset
            ' A scale above what this headset allows runs at the largest that fits (#7f93667d).
            Dim scale = headset.EffectiveScale(settings.VrRenderScale)
            Dim scene = headset.CroppedSceneSize(scale, settings.VrFovWidth(), settings.VrFovHeight())
            context.RequireClosed()
            ' Quality choices are shared with desktop play and kept in the game file (with a backup), so the
            ' VR transaction below records the quality-applied file as the original to restore.
            DesktopGraphics.Apply(context, DesktopGraphics.QualityOnly(settings), Drawing.Size.Empty)
            ' VR-only quality goes into the session's temporary graphics changes and is undone when VR ends.
            Dim changes = ExperimentalFiles.Changes(context, scene.Width, scene.Height, settings.SkipStartupMovies, settings.VrGraphicsQuality, settings.VrAntiAliasing).ToDictionary(Function(pair) pair.Key, Function(pair) pair.Value)
            ' Direct practice: DiRT 3's benchmark start on the chosen route, removed with the other changes.
            If settings.LaunchMode = "direct" Then changes.Add("direct", DirectPractice.Config(settings))
            ' Seat positions: each adjusted car's camera file, for this session only (#2fdaf181).
            Dim seats = SeatPositions.Changes(context, settings)
            For Each pair In seats.Files
                changes.Add(pair.Key, pair.Value)
            Next
            ' Motion blur off: head movement would smear the nearest geometry (#30d34783).
            ' With bloom and/or light streaks + lens flares off for VR (#3c6d6ffa) in the same effects file, and bloom's
            ' generic track files; with the colour filter off (#24f76866), the palettes.
            Dim blurNote As String = ""
            Dim post = PostEffects.SessionChanges(context, motionBlurOff:=True, glareOff:=Not settings.VrGlare, bloomOff:=Not settings.VrBloom, note:=blurNote, colourOff:=Not settings.VrColourFilter)
            For Each pair In post.Files
                changes.Add(pair.Key, pair.Value)
            Next
            Dim effects = If(post.Files.ContainsKey(MotionBlur.Target), post.Files(MotionBlur.Target), Nothing)
            ' Driving controls: the launcher's bindings as session action maps, direct practice only (#8121970e).
            Dim actionMaps = If(settings.LaunchMode = "direct", SessionActionMaps.Changes(context, DrivingControls.Load(context)), New SessionActionMaps.SessionChanges())
            For Each pair In actionMaps.Files
                changes.Add(pair.Key, pair.Value)
            Next
            directory = Path.Combine(context.UserRoot, "logs", "session-" & Guid.NewGuid().ToString("N"))
            SafeFiles.NoLinks(directory) : IO.Directory.CreateDirectory(directory)
            settings.Save(context)
            transaction.Prepare(changes, experimental:=True, apply:=False, vrQuality:=settings.VrGraphicsQuality, seats:=If(seats.Offsets.Count > 0, seats.Offsets, Nothing), effects:=post.Edits)
            staged = True
            Await RecoveryWorker.ApplyAsync(context)
            context.RequireClosed()
            ' SteamVR's automatic desktop theater off for the session (#24867b69); restored after exit.
            theaterNote = SteamVrTheater.Suppress(context, runtime)
            If SteamVrTheater.Pending(context) Then theaterFolder = Path.GetDirectoryName(runtime.ManifestPath)
            ' The in-game seat menu (#bd820b32): its shared memory lives as long as this session.
            If settings.SeatMenuKey <> 0 OrElse settings.SeatMenuButton IsNot Nothing Then seatMenu = New SeatChannel(settings, seats.Offsets)
            game = Process.Start(StartInfo(context, runtime, directory, settings, headset, preflight.Bypasses, seatMenu?.Name, actionMaps.PreferredDevices))
            If game Is Nothing Then Throw New IOException("DiRT 3 could not start.")
            Dim result As New InteractiveSession(context, game, directory, lease) With {.SeatsSkipped = seats.Skipped, .MotionBlurNote = blurNote, .DrivingNote = actionMaps.Note, .TheaterNote = theaterNote, .ScaleNote = headset.ScaleNote(settings.VrRenderScale), .steamVrFolder = theaterFolder, .SeatMenu = seatMenu}
            SafeFiles.SaveJson(Path.Combine(directory, "session.json"), New With {.schema = 1, .mode = "experimental-interactive", .processId = game.Id, .startedUtc = result.StartedUtc, .runtime = runtime.Mode, .vrRenderScale = scale, .requestedVrRenderScale = settings.VrRenderScale, .vrQuality = GraphicsRecommendation.VrQuality(settings), .vrAntiAliasing = settings.VrAntiAliasing, .flatViews = settings.FlatViews(), .vrFovWidth = settings.VrFovWidth(), .vrFovHeight = settings.VrFovHeight(), .headsetWidth = headset.Width, .headsetHeight = headset.Height, .sceneWidth = scene.Width, .sceneHeight = scene.Height, .stereoVerified = False, .launchMode = settings.LaunchMode, .directRoute = If(settings.LaunchMode = "direct", settings.DirectRoute, 0), .directCar = If(settings.LaunchMode = "direct", settings.DirectCar, ""), .directLaps = If(settings.LaunchMode = "direct", DirectPractice.Laps(settings), 0), .directOpponents = If(settings.LaunchMode = "direct", DirectPractice.Opponents(settings), 0), .directOpponentCars = If(settings.LaunchMode = "direct", settings.DirectOpponentCars, ""), .directDifficulty = If(settings.LaunchMode = "direct", DirectPractice.Difficulty(settings), -1), .directAfterFinish = If(settings.LaunchMode = "direct", settings.DirectAfterFinish, ""), .directAssists = If(settings.LaunchMode = "direct", DirectPractice.Assists(settings), ""), .seatCars = seats.Offsets.Keys.ToArray(), .startInCockpit = settings.StartInCockpit, .cockpitSteady = settings.CockpitSteady, .hideDriver = settings.CockpitHideDriver, .hideCoDriver = settings.CockpitHideCoDriver, .seatSkipped = seats.Skipped.ToArray(), .motionBlurOff = effects IsNot Nothing, .motionBlurNote = blurNote, .bloomOff = Not settings.VrBloom, .glareOff = Not settings.VrGlare, .colourOff = Not settings.VrColourFilter, .palettesSwapped = post.Files.ContainsKey(PostEffects.PaletteTarget), .drivingMaps = actionMaps.Devices.Select(Function(d) d.DeviceName & " (" & d.BaseFile & ")").ToArray(), .drivingSkipped = actionMaps.Skipped.ToArray(), .directDevices = actionMaps.PreferredDevices, .disabledApiLayers = preflight.Bypasses.Select(Function(layer) layer.LayerName).ToArray(), .layerWarning = preflight.Warning})
            Session.WriteLaunchStatus(context, "starting", "Waiting for the game window and native headset output.", game.Id, result.StartedUtc, mode:="experimental-interactive")
            Return result
        Catch ex As Exception
            failure = ex
        End Try
        ' A post-start logging failure cannot abandon the owned process.
        If game IsNot Nothing Then Return New InteractiveSession(context, game, directory, lease) With {.steamVrFolder = theaterFolder, .SeatMenu = seatMenu}
        seatMenu?.Dispose()
        If theaterFolder IsNot Nothing Then SteamVrTheater.RestorePending(context, theaterFolder)
        Try
            If staged AndAlso transaction.Pending Then Await RecoveryWorker.RecoverAsync(context)
        Catch recoveryError As Exception
            Throw New IOException(failure.Message & " Recovery is pending: " & recoveryError.Message, failure)
        Finally
            lease.Dispose()
        End Try
        Throw failure
    End Function

    Public Function ReadNativeTail() As String
        Dim path = IO.Path.Combine(LogDirectory, "native-" & Game.Id.ToString(Globalization.CultureInfo.InvariantCulture) & ".log")
        Try
            SafeFiles.NoLinks(path)
            Using stream As New FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite Or FileShare.Delete)
                stream.Position = Math.Max(0, stream.Length - 256 * 1024)
                Using reader As New StreamReader(stream)
                    Return reader.ReadToEnd()
                End Using
            End Using
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is UnauthorizedAccessException
            Return ""
        End Try
    End Function

    Public Shared Function OutputStatus(logs As String) As String
        Dim failures = System.Text.RegularExpressions.Regex.Matches(logs, "XR (?:prototype|screen) (?:stopped|failed|refused)[^\r\n]*")
        If failures.Count > 0 Then
            Dim reason = failures(failures.Count - 1).Value
            If reason.Length > 200 Then reason = reason.Substring(0, 200)
            Return "Experimental headset output stopped: " & reason & ". Quit DiRT 3 to restore original files."
        End If
        Dim frames = System.Text.RegularExpressions.Regex.Matches(logs, "XR screen submitted=[1-9][0-9]*\b|XR prototype main=[0-9]+ submitted=1\b")
        If frames.Count > 0 Then
            If frames(frames.Count - 1).Value.StartsWith("XR screen", StringComparison.Ordinal) Then Return "Virtual screen active · scene stereo is not active"
            Return "Stereo scene frames submitted · original files will be restored after game exit"
        End If
        Return "Experimental VR starting · waiting for native headset output"
    End Function

    Private Function HasExited() As Boolean
        Try
            Return Game.HasExited
        Catch ex As InvalidOperationException
            Return True ' The lifetime monitor already closed the process handle.
        End Try
    End Function

    ' True once the launcher asked the renderer to end VR (Stop VR or End session).
    Public ReadOnly Property VrStopped As Boolean
        Get
            Return stopRequested
        End Get
    End Property

    Private Sub RequestXrStop()
        If stopRequested Then Return
        SafeFiles.NoLinks(StopPath)
        Using stream As New FileStream(StopPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read)
            stream.Write(Encoding.UTF8.GetBytes("launcher-requested shutdown" & vbLf)) : stream.Flush(True)
        End Using
        stopRequested = True
    End Sub

    ' Ends VR but leaves DiRT 3 running on the monitor. The renderer releases the headset and the
    ' game draws normally; the temporary files stay until DiRT 3 closes, then they are restored.
    Public Function StopVr() As String
        If HasExited() Then Return "DiRT 3 has exited; restoring session files."
        RequestXrStop()
        Return "VR stopped. DiRT 3 keeps running on the monitor with the VR graphics settings; original files are restored when it closes."
    End Function

    ' Ends VR, then asks DiRT 3 to close.
    Public Async Function StopAsync() As Task(Of String)
        If HasExited() Then Return "DiRT 3 has exited; restoring session files."
        RequestXrStop()
        Dim deadline = Stopwatch.StartNew()
        While Not HasExited() AndAlso Not ReadNativeTail().Contains("XR prototype shutdown complete", StringComparison.Ordinal) AndAlso deadline.Elapsed < TimeSpan.FromSeconds(3)
            Await Task.Delay(100)
        End While
        If Not HasExited() Then Game.CloseMainWindow()
        Return "Exit requested. Session files will be restored after DiRT 3 closes."
    End Function

    Public Async Function WaitAndRestoreAsync() As Task(Of String)
        Try
            Await Game.WaitForExitAsync()
            Dim exitCode = Game.ExitCode
            ' A seat position saved in the menu just before DiRT 3 closed is kept too.
            Try
                FinalSeatCommit = SeatChannel.SaveCommit(context, SeatMenu)
            Catch ex As IOException
            End Try
            SeatMenu?.Dispose() : SeatMenu = Nothing
            Dim restored As Boolean
            DirectPractice.RemoveResults(context)
            Try
                ' Another DiRT 3 process still closing gets up to 30 s; after that the restore names it and refuses.
                Await InstallContext.WaitUntilClosedAsync(InstallContext.CloseGrace)
                restored = Await RecoveryWorker.RecoverAsync(context)
            Catch ex As Exception
                Session.WriteLaunchStatus(context, "recovery-pending", "DiRT 3 closed; recovery is pending: " & ex.Message, Game.Id, StartedUtc, exitCode, mode:="experimental-interactive")
                Throw
            End Try
            Dim message = If(restored, "DiRT 3 closed; original session files restored.", "DiRT 3 closed; no file recovery was pending.")
            If steamVrFolder IsNot Nothing Then
                Dim theater = SteamVrTheater.RestorePending(context, steamVrFolder)
                If theater <> "" Then message &= " " & theater
            End If
            If exitCode <> 0 Then message &= $" Exit code 0x{exitCode:X8}. Open logs for details."
            Session.WriteLaunchStatus(context, "closed", message, Game.Id, StartedUtc, exitCode, mode:="experimental-interactive")
            Return message
        Finally
            Game.Dispose() : lease.Dispose()
        End Try
    End Function
End Class
