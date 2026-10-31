Imports System.Text

' The UI owns this object until the child exits and recovery finishes.
Public Class InteractiveSession
    Private ReadOnly context As InstallContext
    Private ReadOnly lease As FileStream
    Public ReadOnly Game As Process
    Public ReadOnly LogDirectory As String
    Public ReadOnly StopPath As String
    Public ReadOnly StartedUtc As DateTime
    Private stopRequested As Boolean

    Private Sub New(install As InstallContext, child As Process, folder As String, sessionLease As FileStream)
        context = install : Game = child : LogDirectory = folder : lease = sessionLease
        StopPath = Path.Combine(folder, "xr-stop.request")
        StartedUtc = DateTime.UtcNow
        Try
            StartedUtc = child.StartTime.ToUniversalTime()
        Catch ex As Exception When TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is ComponentModel.Win32Exception
            ' Preserve the process handle even if the child exits immediately.
        End Try
    End Sub

    Public Shared Function StartInfo(context As InstallContext, runtime As RuntimeInfo, directory As String, settings As LauncherSettings, headset As HeadsetResolution) As ProcessStartInfo
        If Not Path.IsPathFullyQualified(directory) Then Throw New IOException("The session log path must be absolute.")
        settings.Validate()
        If headset Is Nothing OrElse headset.RuntimeMode <> runtime.Mode Then Throw New IOException("Query the selected headset before launching.")
        headset.SceneSize(settings.VrRenderScale)
        Dim start = Session.DesktopStartInfo(context)
        start.Environment("DIRT3VR_DIAGNOSTICS") = "1"
        start.Environment("DIRT3VR_SCENE_DIAGNOSTICS") = "1"
        start.Environment("DIRT3VR_PRESENTATION_TRACE") = If(settings.DiagnosticLogging, "1", "0")
        start.Environment("DIRT3VR_XR_PROTOTYPE") = "1"
        start.Environment("DIRT3VR_XR_INTERACTIVE") = "1"
        start.Environment("DIRT3VR_VR_RENDER_SCALE") = settings.VrRenderScale.ToString(Globalization.CultureInfo.InvariantCulture)
        start.Environment("DIRT3VR_HEADSET_WIDTH") = headset.Width.ToString(Globalization.CultureInfo.InvariantCulture)
        start.Environment("DIRT3VR_HEADSET_HEIGHT") = headset.Height.ToString(Globalization.CultureInfo.InvariantCulture)
        start.Environment("DIRT3VR_LOG_DIR") = directory
        start.Environment("DIRT3VR_XR_STOP_FILE") = Path.Combine(directory, "xr-stop.request")
        start.Environment("XR_RUNTIME_JSON") = runtime.ManifestPath
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

    Public Shared Async Function StartAsync(context As InstallContext, settings As LauncherSettings) As Task(Of InteractiveSession)
        RuntimeCapabilities.RequireExperimental(context, settings)
        Dim lease = AcquireLease(context), game As Process = Nothing
        Dim transaction As New OwnedFileTransaction(context)
        Dim directory As String = Nothing, failure As Exception = Nothing, staged As Boolean = False
        Try
            context.RequireClosed() : context.ValidateGame()
            If transaction.Pending Then Throw New IOException("Restore pending session files before launching.")
            Dim runtime = VrRuntime.Resolve(settings.RuntimeMode, settings.SteamRuntimeManifest, settings.VdxrRuntimeManifest)
            ' Always re-query the selected runtime before staging; UI results may be stale.
            Dim headset = Await VrRuntime.ReadHeadsetAsync(context, settings, selectedRuntime:=runtime)
            Dim scene = headset.SceneSize(settings.VrRenderScale)
            context.RequireClosed()
            Dim changes = ExperimentalFiles.Changes(context, scene.Width, scene.Height, settings.SkipStartupMovies)
            directory = Path.Combine(context.UserRoot, "logs", "session-" & Guid.NewGuid().ToString("N"))
            SafeFiles.NoLinks(directory) : IO.Directory.CreateDirectory(directory)
            settings.Save(context)
            transaction.Prepare(changes, experimental:=True, apply:=False)
            staged = True
            Await RecoveryWorker.ApplyAsync(context)
            context.RequireClosed()
            game = Process.Start(StartInfo(context, runtime, directory, settings, headset))
            If game Is Nothing Then Throw New IOException("DiRT 3 could not start.")
            Dim result As New InteractiveSession(context, game, directory, lease)
            SafeFiles.SaveJson(Path.Combine(directory, "session.json"), New With {.schema = 1, .mode = "experimental-interactive", .processId = game.Id, .startedUtc = result.StartedUtc, .runtime = runtime.Mode, .vrRenderScale = settings.VrRenderScale, .headsetWidth = headset.Width, .headsetHeight = headset.Height, .sceneWidth = scene.Width, .sceneHeight = scene.Height, .stereoVerified = False})
            Session.WriteLaunchStatus(context, "starting", "Waiting for the game window and native headset output.", game.Id, result.StartedUtc, mode:="experimental-interactive")
            Return result
        Catch ex As Exception
            failure = ex
        End Try
        ' A post-start logging failure cannot abandon the owned process.
        If game IsNot Nothing Then Return New InteractiveSession(context, game, directory, lease)
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

    Public Async Function StopAsync() As Task(Of String)
        If HasExited() Then Return "DiRT 3 has exited; restoring session files."
        If Not stopRequested Then
            SafeFiles.NoLinks(StopPath)
            Using stream As New FileStream(StopPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read)
                stream.Write(Encoding.UTF8.GetBytes("launcher-requested shutdown" & vbLf)) : stream.Flush(True)
            End Using
            stopRequested = True
        End If
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
            Dim restored = Await RecoveryWorker.RecoverAsync(context)
            Dim message = If(restored, "DiRT 3 closed; original session files restored.", "DiRT 3 closed; no file recovery was pending.")
            If exitCode <> 0 Then message &= $" Exit code 0x{exitCode:X8}. Open logs for details."
            Session.WriteLaunchStatus(context, "closed", message, Game.Id, StartedUtc, exitCode, mode:="experimental-interactive")
            Return message
        Finally
            Game.Dispose() : lease.Dispose()
        End Try
    End Function
End Class
