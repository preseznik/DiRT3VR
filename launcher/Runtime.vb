Imports System.Text.Json
Imports System.Threading

Public Class RuntimeInfo
    Public Property Mode As String = ""
    Public Property ManifestPath As String = ""
    Public Property Name As String = ""
End Class

Public Class RuntimeCapabilities
    Public Property Schema As Integer
    Public Property SupportedExeSha256 As String = ""
    Public Property StereoVerified As Boolean
    Public Property Runtimes As String() = {}
    Public Property ExperimentalInteractive As Boolean

    ' A manifest cannot turn an unimplemented native launch path into a VR feature.
    Public Const StereoLaunchImplemented As Boolean = False
    Public Const UnavailableReason As String = "VR driving is not available in this build. Use Launch for normal desktop play. Checking a runtime does not enable VR."
    ' SteamVR is allowed for Launch VR again (#da2bcde1): the 2026-10-28 upside-down report came from a run with
    ' GRID2VR open at the same time and was never retested alone; the renderer logs the runtime pose, recenter
    ' reference, eye FOV and camera basis to locate any inversion.
    Public Const SteamVrNote As String = "SteamVR is new in DiRT3VR: if the picture is upside down, tilted or mirrored, end the session and report it with Open logs."

    Public Shared Function ExperimentalReason(context As InstallContext, settings As LauncherSettings) As String
        If Not settings.ExperimentalVrEnabled Then Return "Enable Experimental VR in Advanced to use Launch VR."
        Try
            Dim capability = Read(context)
            If Not capability.ExperimentalInteractive OrElse Not capability.Runtimes.Contains(settings.RuntimeMode) Then
                Return "This package does not include interactive experimental VR for " & VrRuntime.Label(settings.RuntimeMode) & ". Desktop launch and runtime checks remain available."
            End If
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is JsonException OrElse TypeOf ex Is UnauthorizedAccessException
            Return ex.Message
        End Try
        Return ""
    End Function

    Public Shared Sub RequireExperimental(context As InstallContext, settings As LauncherSettings)
        settings.Validate()
        Dim reason = ExperimentalReason(context, settings)
        If reason <> "" Then Throw New IOException(reason)
    End Sub

    Public Shared Function Read(context As InstallContext) As RuntimeCapabilities
        Dim path = IO.Path.Combine(context.PackageRoot, "runtime-capabilities.json")
        If Not File.Exists(path) Then Return New RuntimeCapabilities()
        Dim value = SafeFiles.ReadJson(Of RuntimeCapabilities)(path)
        If value.Schema <> 1 OrElse Not String.Equals(value.SupportedExeSha256, InstallContext.SupportedHash, StringComparison.OrdinalIgnoreCase) OrElse value.Runtimes Is Nothing Then Throw New IOException("The runtime capability file does not match this launcher.")
        Return value
    End Function

    Public Shared Sub RequireStereo(context As InstallContext)
        If Not StereoLaunchImplemented Then Throw New IOException(UnavailableReason)
        If Not Read(context).StereoVerified Then Throw New IOException(UnavailableReason)
    End Sub
End Class

Public Module VrRuntime
    ' VDXR, SteamVR, and any other 32-bit OpenXR runtime the player browses to (#982a5c40).
    Public ReadOnly Modes As String() = {"vdxr", "steamvr", "other"}
    Public Const OtherName As String = "Other OpenXR runtime"

    Public Function Label(mode As String) As String
        Return If(mode = "steamvr", "SteamVR", If(mode = "other", "the other OpenXR runtime", "VDXR"))
    End Function

    Public Function Resolve(mode As String, steamManifest As String, Optional vdxrManifest As String = "", Optional otherManifest As String = "") As RuntimeInfo
        If mode = "openxr" Then mode = "vdxr" ' Compatibility with initial settings and command lines.
        If Not Modes.Contains(mode) Then Throw New IOException("Choose VDXR, SteamVR or another OpenXR runtime.")
        If mode = "other" Then
            ' Any runtime manifest, as long as its library is 32-bit (ReadManifest); the runtime check shows the rest.
            If String.IsNullOrWhiteSpace(otherManifest) Then Throw New IOException("Select the OpenXR runtime's .json file in Settings → VR runtime, then check the runtime and headset.")
            If Not String.Equals(IO.Path.GetExtension(otherManifest), ".json", StringComparison.OrdinalIgnoreCase) Then Throw New IOException("Select the OpenXR runtime's .json manifest file.")
            If Not File.Exists(otherManifest) Then Throw New IOException("The OpenXR runtime manifest was not found: " & otherManifest)
            Return ReadManifest(mode, otherManifest)
        End If
        Dim path = If(mode = "steamvr", If(String.IsNullOrWhiteSpace(steamManifest), Discovery.SteamRuntime(), steamManifest), If(String.IsNullOrWhiteSpace(vdxrManifest), Discovery.VdxrRuntime(), vdxrManifest))
        Dim expected = If(mode = "steamvr", "steamxr_win32.json", "virtualdesktop-openxr-32.json")
        If String.IsNullOrWhiteSpace(path) OrElse Not File.Exists(path) Then Throw New IOException(If(mode = "vdxr", "VDXR's 32-bit runtime was not found. Install Virtual Desktop Streamer or select its virtualdesktop-openxr-32.json file in Settings.", "SteamVR's 32-bit runtime was not found. Install SteamVR or select its steamxr_win32.json file in Settings."))
        If Not String.Equals(IO.Path.GetFileName(path), expected, StringComparison.OrdinalIgnoreCase) Then Throw New IOException("Select " & expected & " for " & If(mode = "vdxr", "VDXR", "SteamVR") & ".")
        Return ReadManifest(mode, path)
    End Function

    Public Function ReadManifest(mode As String, path As String) As RuntimeInfo
        If Not IO.Path.IsPathFullyQualified(path) Then Throw New IOException("The runtime manifest path must be absolute.")
        If Not File.Exists(path) Then Throw New IOException("Runtime manifest not found: " & path)
        If New FileInfo(path).Length > 1024 * 1024 Then Throw New IOException("Runtime manifest is too large.")
        Using document = JsonDocument.Parse(File.ReadAllText(path))
            Dim runtime = document.RootElement.GetProperty("runtime")
            Dim library = runtime.GetProperty("library_path").GetString()
            If String.IsNullOrWhiteSpace(library) Then Throw New IOException("The runtime manifest has no library path.")
            library = IO.Path.GetFullPath(IO.Path.Combine(IO.Path.GetDirectoryName(path), library))
            Using stream = File.OpenRead(library), reader As New BinaryReader(stream)
                If stream.Length < 64 OrElse reader.ReadUInt16() <> &H5A4D Then Throw New IOException("The runtime library is not a Windows DLL.")
                stream.Position = &H3C
                Dim pe = reader.ReadInt32()
                If pe < 64 OrElse pe > stream.Length - 6 Then Throw New IOException("Invalid runtime PE header.")
                stream.Position = pe
                If reader.ReadUInt32() <> &H4550UI OrElse reader.ReadUInt16() <> &H14C Then Throw New IOException("DiRT 3 requires a 32-bit (x86) OpenXR runtime.")
            End Using
            Dim name = If(mode = "steamvr", "SteamVR", If(mode = "other", OtherName, "VDXR"))
            Dim suppliedName As JsonElement
            If runtime.TryGetProperty("name", suppliedName) AndAlso suppliedName.ValueKind = JsonValueKind.String Then name = suppliedName.GetString()
            Return New RuntimeInfo With {.Mode = mode, .ManifestPath = IO.Path.GetFullPath(path), .Name = name}
        End Using
    End Function

    Public Async Function ProbeAsync(context As InstallContext, settings As LauncherSettings, Optional cancellation As CancellationToken = Nothing) As Task(Of String)
        Dim headset = Await ReadHeadsetAsync(context, settings, cancellation)
        Return headset.Summary(settings.VrRenderScale) & Environment.NewLine & "No game or VR session was started."
    End Function

    Public Async Function ReadHeadsetAsync(context As InstallContext, settings As LauncherSettings, Optional cancellation As CancellationToken = Nothing, Optional selectedRuntime As RuntimeInfo = Nothing, Optional layers As IReadOnlyList(Of VrLayerBypass) = Nothing) As Task(Of HeadsetResolution)
        settings.Validate()
        Dim runtime = If(selectedRuntime, Resolve(settings.RuntimeMode, settings.SteamRuntimeManifest, settings.VdxrRuntimeManifest, settings.OtherRuntimeManifest))
        Dim start = ProbeStartInfo(context, runtime, layers)
        Dim disabledLayers = If(layers, Array.Empty(Of VrLayerBypass)()).Select(Function(layer) layer.LayerName).ToArray()
        Using child As New Process With {.StartInfo = start}
            If Not child.Start() Then Throw New IOException("The runtime probe could not start.")
            Using cancelled = cancellation.Register(Sub()
                                                       Try
                                                           If Not child.HasExited Then child.Kill(True)
                                                       Catch ex As Exception When TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is System.ComponentModel.Win32Exception
                                                           ' The owned probe already exited while the launcher was closing.
                                                       End Try
                                                   End Sub)
                Dim outputTask = ReadBoundedAsync(child.StandardOutput)
                Dim errorTask = ReadBoundedAsync(child.StandardError)
                Dim timedOut = False
                Using timeout As New CancellationTokenSource(TimeSpan.FromSeconds(45))
                    Try
                        Await child.WaitForExitAsync(timeout.Token)
                    Catch ex As OperationCanceledException
                        If Not child.HasExited Then child.Kill(True)
                        timedOut = True
                    End Try
                End Using
                Await child.WaitForExitAsync()
                Dim output = Await outputTask
                Dim errorOutput = Await errorTask
                cancellation.ThrowIfCancellationRequested()
                If timedOut Then Throw New IOException("The runtime check timed out after 45 seconds. No game was started.")
                Dim report As JsonElement
                Try
                    report = ValidateProbeOutput(output, child.ExitCode, errorOutput, runtime.Mode)
                Catch ex As IOException
                    SafeFiles.SaveJson(context.DiagnosticsPath, New With {.schema = 1, .kind = "runtime-probe", .updatedUtc = DateTime.UtcNow, .runtime = runtime.Mode, .disabledApiLayers = disabledLayers, .report = New With {.success = False, .error = ex.Message, .exitCode = child.ExitCode, .stereo = False}})
                    ' Keep the full output: it names an OpenXR add-on that failed to load.
                    Throw New RuntimeProbeException(ex.Message, child.ExitCode, output & Environment.NewLine & errorOutput, ex)
                End Try
                SafeFiles.SaveJson(context.DiagnosticsPath, New With {.schema = 1, .kind = "runtime-probe", .updatedUtc = DateTime.UtcNow, .runtime = runtime.Mode, .disabledApiLayers = disabledLayers, .report = report})
                Return HeadsetResolution.FromReport(report, runtime.Mode)
            End Using
        End Using
    End Function

    Public Function ProbeStartInfo(context As InstallContext, runtime As RuntimeInfo, Optional layers As IReadOnlyList(Of VrLayerBypass) = Nothing) As ProcessStartInfo
        Dim executable = IO.Path.Combine(context.PackageRoot, "payload", "DiRT3VR.RuntimeProbe.exe")
        SafeFiles.NoLinks(executable)
        If Not File.Exists(executable) Then Throw New IOException("The x86 runtime probe is not included in this build. The selected manifest is valid; headset availability has not been checked.")
        Dim start As New ProcessStartInfo(executable) With {.UseShellExecute = False, .CreateNoWindow = True, .RedirectStandardOutput = True, .RedirectStandardError = True, .WorkingDirectory = context.PackageRoot}
        start.ArgumentList.Add("--runtime-json") : start.ArgumentList.Add(runtime.ManifestPath)
        start.Environment("XR_RUNTIME_JSON") = runtime.ManifestPath
        ' Loader warnings name an OpenXR add-on whose library failed to load. Keep a more detailed level.
        Dim verbosity As String = Nothing
        start.Environment.TryGetValue("XR_LOADER_DEBUG", verbosity)
        If Not {"warn", "info", "verbose", "all"}.Contains(verbosity) Then start.Environment("XR_LOADER_DEBUG") = "warn"
        VrApiLayers.Configure(start, layers)
        Return start
    End Function

    ' The probe writes one JSON line; with XR_LOADER_DEBUG the OpenXR loader adds its own lines.
    Public Function ProbeJson(output As String) As String
        Dim trimmed = output.Trim()
        If trimmed.StartsWith("{"c) AndAlso trimmed.EndsWith("}"c) AndAlso Not trimmed.Contains(ControlChars.Lf) Then Return trimmed
        Dim lines = output.Split({vbCrLf, vbLf}, StringSplitOptions.None)
        Dim report = lines.LastOrDefault(Function(line) line.TrimStart().StartsWith("{""schema"":", StringComparison.Ordinal))
        Return If(report, trimmed).Trim()
    End Function

    ' XR_ERROR_FORM_FACTOR_UNAVAILABLE: the runtime is installed but has no headset (#07626461: a Steam Frame player
    ' still on VDXR, the default). Says what to do for the chosen runtime instead of the raw OpenXR code.
    Public Const NoHeadsetResult As Integer = -35

    Public Function NoHeadsetHint(mode As String) As String
        Select Case mode
            Case "steamvr" : Return "SteamVR reports no headset. Start SteamVR with your headset connected and try again. If you connect through Virtual Desktop, choose VDXR under Settings → VR runtime."
            Case "other" : Return "The other OpenXR runtime reports no headset. Start its software with your headset connected and try again, or choose VDXR or SteamVR under Settings → VR runtime."
            Case Else : Return "No headset is connected through Virtual Desktop (VDXR). Connect your headset in Virtual Desktop and try again. If your headset uses SteamVR (Steam Frame, Valve Index, HTC Vive, or Pico through SteamVR), choose SteamVR under Settings → VR runtime."
        End Select
    End Function

    ' mode: the runtime that was checked, for a clearer message when it has no headset.
    Public Function ValidateProbeOutput(output As String, exitCode As Integer, errors As String, Optional mode As String = Nothing) As JsonElement
        If output.Length > 1024 * 1024 Then Throw New IOException("Runtime probe returned too much output.")
        Try
            Using document = JsonDocument.Parse(ProbeJson(output))
                Dim success As JsonElement, stereo As JsonElement
                Dim root = document.RootElement
                If root.ValueKind <> JsonValueKind.Object OrElse Not root.TryGetProperty("success", success) OrElse (success.ValueKind <> JsonValueKind.True AndAlso success.ValueKind <> JsonValueKind.False) Then Throw New IOException("The runtime probe returned an invalid report.")
                If exitCode <> 0 OrElse Not success.GetBoolean() Then
                    Dim detail As JsonElement
                    Dim explanation = If(root.TryGetProperty("error", detail) AndAlso detail.ValueKind = JsonValueKind.String, detail.GetString(), errors.Trim())
                    Dim result As JsonElement, code As Integer
                    Dim noHeadset = If(explanation, "").Contains("XR_ERROR_FORM_FACTOR_UNAVAILABLE", StringComparison.Ordinal) OrElse
                                    (root.TryGetProperty("xrResult", result) AndAlso result.ValueKind = JsonValueKind.Number AndAlso result.TryGetInt32(code) AndAlso code = NoHeadsetResult)
                    If noHeadset Then Throw New IOException("Runtime check failed. " & NoHeadsetHint(mode) & " (XR_ERROR_FORM_FACTOR_UNAVAILABLE)")
                    Throw New IOException("Runtime check failed. " & If(String.IsNullOrWhiteSpace(explanation), "Check that your selected runtime and headset are ready.", explanation))
                End If
                If Not root.TryGetProperty("stereo", stereo) OrElse stereo.ValueKind <> JsonValueKind.False Then Throw New IOException("The runtime probe returned an unexpected stereo capability. Game VR remains unavailable.")
                Return root.Clone()
            End Using
        Catch ex As JsonException
            Throw New IOException("The runtime probe did not return a valid JSON report.", ex)
        End Try
    End Function

    Private Async Function ReadBoundedAsync(reader As StreamReader) As Task(Of String)
        Dim result As New Text.StringBuilder()
        Dim buffer(4095) As Char
        Dim overflow = False
        Do
            Dim count = Await reader.ReadAsync(buffer.AsMemory())
            If count = 0 Then Exit Do
            If result.Length + count <= 1024 * 1024 Then
                result.Append(buffer, 0, count)
            Else
                overflow = True ' Continue draining so a noisy child cannot deadlock.
            End If
        Loop
        If overflow Then Throw New IOException("Runtime probe output exceeded its limit.")
        Return result.ToString()
    End Function
End Module
