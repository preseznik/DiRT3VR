Imports DiRT3VR
Imports System.IO
Imports System.Windows.Forms

Module VrLayerTests
    Private Const ReshadeDll As String = "C:\Program Files\ReShade\ReShade32.dll"

    Private Function Context(root As String, name As String) As InstallContext
        Dim game = Path.Combine(root, name)
        Directory.CreateDirectory(game)
        Return New InstallContext(game, Path.Combine(root, name & "-users"), Path.Combine(game, "graphics.xml"), root)
    End Function

    Private Function Refused(action As Action, Optional contains As String = Nothing) As Boolean
        Try
            action()
        Catch ex As IOException
            Return contains Is Nothing OrElse ex.Message.Contains(contains)
        End Try
        Return False
    End Function

    ' What the 32-bit probe prints when ReShade's library is missing (pinned OpenXR loader text).
    Private Function LayerFailureReport(Optional result As Integer = -32, Optional path As String = ReshadeDll) As String
        Return "Warning [GENERAL | xrCreateInstance | OpenXR-Loader] : ApiLayerInterface::LoadApiLayers skipping layer XR_APILAYER_reshade, failed to load with message ""Failed to open dynamic library " & path & " with error 126: The specified module could not be found." & vbCrLf & """" & vbCrLf &
               "Error [GENERAL | xrCreateInstance | OpenXR-Loader] : Failed loading layer information" & vbCrLf &
               "{""schema"":1,""success"":false,""architecture"":""x86"",""stereo"":false,""sessionCreated"":false,""operation"":""createInstance"",""xrResult"":" & result & ",""error"":""XR_ERROR_FILE_ACCESS_ERROR""}" & vbLf
    End Function

    Private Function Manifest(folder As String, name As String, library As String, variable As String) As String
        Directory.CreateDirectory(folder)
        Dim file = Path.Combine(folder, name & ".json")
        IO.File.WriteAllText(file, "{""file_format_version"":""1.0.0"",""api_layer"":{""name"":""" & name & """,""library_path"":""" & library.Replace("\", "\\") & """,""api_version"":""1.0"",""implementation_version"":""1"",""description"":""test"",""disable_environment"":""" & variable & """}}")
        Return file
    End Function

    Public Sub Run(root As String, check As Action(Of Boolean, String))
        For Each good In {"DISABLE_XR_APILAYER_RESHADE", "DISABLE_OPENXR_OBS_MIRROR"}
            check(VrApiLayers.SafeDisableVariable(good), "add-on disable switch accepted: " & good)
        Next
        For Each bad In {Nothing, "", "PATH", "XR_RUNTIME_JSON", "DISABLE_", "DISABLE_XR_", "DISABLE_XR_A B", "disable_xr_lower", "DISABLE_XR_" & New String("A"c, 181)}
            check(Not VrApiLayers.SafeDisableVariable(bad), "only DISABLE_XR_/DISABLE_OPENXR_ switches are used: " & If(bad, "(null)"))
        Next

        Dim folder = Path.Combine(root, "openxr-layers")
        Dim reshadeManifest = Manifest(folder, "XR_APILAYER_reshade", ReshadeDll, "DISABLE_XR_APILAYER_RESHADE")
        Dim reshade = VrApiLayers.ReadManifest(reshadeManifest)
        check(reshade IsNot Nothing AndAlso reshade.LayerName = "XR_APILAYER_reshade" AndAlso reshade.DisplayName = "ReShade" AndAlso reshade.LibraryPath = ReshadeDll AndAlso reshade.DisableVariable = "DISABLE_XR_APILAYER_RESHADE" AndAlso reshade.ManifestHash.Length = 64, "an add-on manifest is read")
        Dim relative = VrApiLayers.ReadManifest(Manifest(Path.Combine(folder, "relative"), "XR_APILAYER_relative", "bin\layer32.dll", "DISABLE_XR_RELATIVE"))
        check(relative IsNot Nothing AndAlso relative.LibraryPath = Path.Combine(folder, "relative", "bin", "layer32.dll"), "a relative library path is resolved beside its manifest")
        check(VrApiLayers.ReadManifest(Manifest(folder, "not_a_layer", ReshadeDll, "DISABLE_XR_X")) Is Nothing, "a manifest must name an XR_APILAYER_ add-on")
        check(VrApiLayers.ReadManifest(Manifest(Path.Combine(folder, "unsafe"), "XR_APILAYER_unsafe", ReshadeDll, "PATH")) Is Nothing, "a manifest cannot redirect the bypass to another variable")
        check(VrApiLayers.ReadManifest("relative\manifest.json") Is Nothing AndAlso VrApiLayers.ReadManifest(Path.Combine(folder, "missing.json")) Is Nothing, "missing or relative manifests are ignored")
        check(VrApiLayers.Discover() IsNot Nothing, "reading the registered add-ons never throws")

        Dim failures = VrApiLayers.Failures(1, LayerFailureReport())
        check(failures.Count = 1 AndAlso failures(0).LayerName = "XR_APILAYER_reshade" AndAlso failures(0).LibraryPath = ReshadeDll AndAlso failures(0).WindowsError = 126, "the probe report names the add-on that failed to load")
        check(VrApiLayers.Describe(failures) = "An OpenXR add-on could not load: XR_APILAYER_reshade (Windows error 126).", "the failure is described plainly")
        check(VrApiLayers.Failures(0, LayerFailureReport()).Count = 0 AndAlso VrApiLayers.Failures(1, LayerFailureReport(-2)).Count = 0 AndAlso
              VrApiLayers.Failures(1, LayerFailureReport().Replace("Failed loading layer information", "")).Count = 0 AndAlso VrApiLayers.Failures(1, LayerFailureReport(path:="relative\x.dll")).Count = 0,
              "only a createInstance file-access failure with loader evidence names an add-on")
        check(VrApiLayers.Failures(1, LayerFailureReport() & LayerFailureReport()).Count = 1, "repeated loader lines are counted once")

        Dim catalog As New List(Of VrLayerBypass) From {reshade}
        Dim resolved = VrApiLayers.Resolve(failures, catalog)
        check(resolved.Count = 1 AndAlso resolved(0).SameIdentity(reshade), "the failure resolves to its registered add-on")
        Dim moved = VrApiLayers.Failures(1, LayerFailureReport(path:="C:\Elsewhere\ReShade32.dll"))
        check(VrApiLayers.Resolve(moved, catalog).Count = 0, "a different library path is not the registered add-on")
        Dim sharing = VrApiLayers.ReadManifest(Manifest(Path.Combine(folder, "sharing"), "XR_APILAYER_other", "C:\Other\other.dll", "DISABLE_XR_APILAYER_RESHADE"))
        check(VrApiLayers.Resolve(failures, {reshade, sharing}).Count = 0, "a switch shared with another add-on is never used")
        check(VrApiLayers.Revalidate({reshade}, catalog).Count = 1, "an unchanged remembered approval still applies")
        IO.File.AppendAllText(reshadeManifest, " ")
        check(VrApiLayers.Revalidate({reshade}, {VrApiLayers.ReadManifest(reshadeManifest)}).Count = 0, "a changed manifest needs a new approval")

        Dim start As New Diagnostics.ProcessStartInfo("game.exe")
        VrApiLayers.Configure(start, {reshade})
        check(start.Environment("DISABLE_XR_APILAYER_RESHADE") = "1", "the bypass sets only the add-on's own switch")
        check(Refused(Sub() VrApiLayers.Configure(start, {New VrLayerBypass With {.DisableVariable = "PATH"}}), "Invalid"), "an unsafe switch is refused")

        check(VrRuntime.ProbeJson(LayerFailureReport()).StartsWith("{""schema"":1,", StringComparison.Ordinal), "the probe's JSON line is found among loader messages")
        check(Refused(Sub() VrRuntime.ValidateProbeOutput(LayerFailureReport(), 1, ""), "Runtime check failed"), "a probe failure with loader messages is still reported")
        Dim okOutput = "Warning [GENERAL | xrCreateInstance | OpenXR-Loader] : something" & vbLf & "{""schema"":1,""success"":true,""stereo"":false,""runtimeName"":""test""}" & vbLf
        check(VrRuntime.ValidateProbeOutput(okOutput, 0, "").GetProperty("runtimeName").GetString() = "test", "a successful probe with loader warnings is accepted")

        ' The preflight: approve, remember, cancel, unrecognised add-on, still failing, registration changed.
        Dim ctx = Context(root, "layer-preflight")
        Dim reshadeNow = VrApiLayers.ReadManifest(reshadeManifest)
        Dim discover = Function() New List(Of VrLayerBypass) From {VrApiLayers.ReadManifest(reshadeManifest)}
        Dim headset As New HeadsetResolution("vdxr", "Test headset", 2400, 2600)
        Dim calls As New List(Of String)
        Dim blockedUnlessDisabled As Func(Of IReadOnlyList(Of VrLayerBypass), Task(Of HeadsetResolution)) =
            Function(layers)
                calls.Add(String.Join(",", layers.Select(Function(l) l.LayerName)))
                If layers.Any(Function(l) l.LayerName = "XR_APILAYER_reshade") Then Return Task.FromResult(headset)
                Return Task.FromException(Of HeadsetResolution)(New RuntimeProbeException("Runtime check failed. XR_ERROR_FILE_ACCESS_ERROR", 1, LayerFailureReport()))
            End Function
        Dim asked = 0
        Dim result = VrLayerPreflight.ReadHeadsetAsync(ctx, blockedUnlessDisabled, Function(request)
                                                                                    asked += 1
                                                                                    check(request.Layers.Single().DisplayName = "ReShade" AndAlso request.Report.Contains("skipping layer"), "the prompt names the add-on and keeps the details")
                                                                                    Return New VrLayerRecoveryChoice With {.Retry = True, .Remember = True}
                                                                                End Function, discover).GetAwaiter().GetResult()
        check(asked = 1 AndAlso calls.SequenceEqual({"", "XR_APILAYER_reshade"}) AndAlso result.Headset Is headset AndAlso result.Bypasses.Single().LayerName = "XR_APILAYER_reshade", "an approved add-on is turned off and the check retried")
        check(VrLayerPreferences.Load(ctx).Bypasses.Single().SameIdentity(reshadeNow), "a remembered approval is saved")
        calls.Clear()
        result = VrLayerPreflight.ReadHeadsetAsync(ctx, blockedUnlessDisabled, Function(request) Nothing, discover).GetAwaiter().GetResult()
        check(calls.SequenceEqual({"XR_APILAYER_reshade"}) AndAlso result.Bypasses.Count = 1, "a remembered approval applies without asking")
        VrLayerPreferences.Reenable(ctx, "XR_APILAYER_reshade")
        check(VrLayerPreferences.Load(ctx).Bypasses.Count = 0, "a remembered add-on can be turned back on")

        check(Refused(Sub() VrLayerPreflight.ReadHeadsetAsync(ctx, blockedUnlessDisabled, Function(request) New VrLayerRecoveryChoice With {.Retry = False}, discover).GetAwaiter().GetResult(), "VR was not started"), "cancel leaves the add-on on and VR off")
        check(Refused(Sub() VrLayerPreflight.ReadHeadsetAsync(ctx, blockedUnlessDisabled, Nothing, discover).GetAwaiter().GetResult(), "Launch VR from the launcher window"), "no silent bypass without a window to ask in")
        check(Refused(Sub() VrLayerPreflight.ReadHeadsetAsync(ctx, blockedUnlessDisabled, Function(request) New VrLayerRecoveryChoice With {.Retry = True}, Function() New List(Of VrLayerBypass)()).GetAwaiter().GetResult(), "can't turn it off automatically"), "an unregistered add-on is explained, not bypassed")
        Dim alwaysFails As Func(Of IReadOnlyList(Of VrLayerBypass), Task(Of HeadsetResolution)) = Function(layers) Task.FromException(Of HeadsetResolution)(New RuntimeProbeException("Runtime check failed. Headset not found", 1, LayerFailureReport()))
        check(Refused(Sub() VrLayerPreflight.ReadHeadsetAsync(ctx, alwaysFails, Function(request) New VrLayerRecoveryChoice With {.Retry = True}, discover).GetAwaiter().GetResult(), "still could not start"), "a retry that still fails reports both problems")
        Dim changing = 0
        Dim changed = Function()
                          changing += 1
                          If changing > 1 Then IO.File.AppendAllText(reshadeManifest, " ")
                          Return New List(Of VrLayerBypass) From {VrApiLayers.ReadManifest(reshadeManifest)}
                      End Function
        check(Refused(Sub() VrLayerPreflight.ReadHeadsetAsync(ctx, blockedUnlessDisabled, Function(request) New VrLayerRecoveryChoice With {.Retry = True}, changed).GetAwaiter().GetResult(), "registration changed"), "an add-on changed while the prompt was open is not bypassed")
        Dim other As Func(Of IReadOnlyList(Of VrLayerBypass), Task(Of HeadsetResolution)) = Function(layers) Task.FromException(Of HeadsetResolution)(New RuntimeProbeException("Runtime check failed. No headset", 1, "{""schema"":1,""success"":false,""operation"":""getSystem"",""xrResult"":-35}"))
        Dim original = False
        Try
            VrLayerPreflight.ReadHeadsetAsync(ctx, other, Function(request) New VrLayerRecoveryChoice With {.Retry = True}, discover).GetAwaiter().GetResult()
        Catch ex As RuntimeProbeException When ex.Message = "Runtime check failed. No headset"
            original = True
        End Try
        check(original, "other runtime failures keep their own message")
        check(VrLayerPreferences.Load(ctx).Bypasses.Count = 0, "nothing is remembered without approval")
        IO.File.WriteAllText(Path.Combine(ctx.UserRoot, "vr-layer-bypasses.json"), "{broken")
        check(VrLayerPreferences.Load(ctx).Bypasses.Count = 0, "unreadable preferences turn nothing off")
    End Sub

    Public Sub RunUi(root As String, check As Action(Of Boolean, String))
        Dim folder = Path.Combine(root, "openxr-layers-ui")
        Dim reshade = VrApiLayers.ReadManifest(Manifest(folder, "XR_APILAYER_reshade", ReshadeDll, "DISABLE_XR_APILAYER_RESHADE"))
        Using prompt As New VrLayerRecoveryForm(New VrLayerRecoveryRequest With {.Layers = {reshade}, .Report = "loader details"})
            prompt.ShowInTaskbar = False : prompt.StartPosition = FormStartPosition.Manual : prompt.Location = New Drawing.Point(-30000, -30000)
            prompt.Show() : Application.DoEvents()
            Dim consequences = DirectCast(prompt.Controls.Find("LayerConsequences", True).Single(), Label).Text
            check(consequences.Contains("ReShade") AndAlso consequences.Contains("won't apply to DiRT 3"), "the prompt names the add-on and what turning it off means")
            check(prompt.AcceptButton Is prompt.CancelButton AndAlso DirectCast(prompt.CancelButton, Button).Name = "CancelLayerRecovery", "Enter cancels; only Disable and retry turns it off")
            Dim details = DirectCast(prompt.Controls.Find("LayerFailureDetails", True).Single(), TextBox)
            DirectCast(prompt.Controls.Find("ShowLayerDetails", True).Single(), Button).PerformClick()
            check(details.Visible AndAlso details.Text = "loader details", "Show details reveals the loader report")
            prompt.Close()
        End Using

        Dim ctx = Context(root, "layer-ui")
        VrLayerPreferences.Remember(ctx, {reshade})
        Using compatibility As New VrCompatibilityForm(ctx)
            compatibility.ShowInTaskbar = False : compatibility.StartPosition = FormStartPosition.Manual : compatibility.Location = New Drawing.Point(-30000, -30000)
            compatibility.Show() : Application.DoEvents()
            Dim list = DirectCast(compatibility.Controls.Find("RememberedLayerBypasses", True).Single(), ListBox)
            check(list.Items.Count = 1 AndAlso list.Items(0).ToString() = "ReShade", "remembered add-ons are listed")
            compatibility.TurnBackOn()
            check(list.Items.Count = 0 AndAlso VrLayerPreferences.Load(ctx).Bypasses.Count = 0 AndAlso DirectCast(compatibility.Controls.Find("LayerBypassSummary", True).Single(), Label).Text.StartsWith("No OpenXR add-ons", StringComparison.Ordinal), "Turn back on removes the remembered choice")
            compatibility.Close()
        End Using

        VrLayerPreferences.Remember(ctx, {reshade})
        Call New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = ""}.Save(ctx)
        Using form As New MainForm(ctx)
            form.ShowInTaskbar = False : form.StartPosition = FormStartPosition.Manual : form.Location = New Drawing.Point(-30000, -30000)
            form.Show() : Application.DoEvents()
            check(DirectCast(form.Controls.Find("LayerSummary", True).Single(), Label).Text = "Turned off for DiRT 3: ReShade. Other apps still use them." AndAlso form.Controls.Find("VrCompatibility", True).Length = 1, "Settings shows which add-ons are turned off for DiRT 3")
            form.Close()
        End Using
        Dim headset As New HeadsetResolution("vdxr", "Test headset", 2400, 2600)
        Dim runtime As New RuntimeInfo With {.Mode = "vdxr", .ManifestPath = "C:\runtime\virtualdesktop-openxr-32.json"}
        Dim settings As New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = ""}
        Dim game = InteractiveSession.StartInfo(ctx, runtime, Path.Combine(ctx.UserRoot, "logs", "fixture"), settings, headset, {reshade})
        check(game.Environment("DISABLE_XR_APILAYER_RESHADE") = "1", "the game gets the same add-on bypass as the probe")
    End Sub
End Module
