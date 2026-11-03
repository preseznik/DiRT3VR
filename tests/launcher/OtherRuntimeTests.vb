Imports DiRT3VR
Imports System.IO
Imports System.Text.Json
Imports System.Windows.Forms

' "Other OpenXR runtime" (#982a5c40): a third VR runtime choice where the player browses to any OpenXR runtime's
' manifest; only its library must be 32-bit. The headset check accepts whatever runtime it names.
Module OtherRuntimeTests
    Private Function Refused(action As Action) As Boolean
        Try
            action()
        Catch ex As IOException
            Return True
        End Try
        Return False
    End Function

    Private Sub WriteDll(path As String, machine As UShort)
        Using stream = File.Create(path), writer As New BinaryWriter(stream)
            stream.SetLength(128) : writer.Write(CUShort(&H5A4D)) : stream.Position = &H3C
            writer.Write(64) : stream.Position = 64 : writer.Write(&H4550UI) : writer.Write(machine)
        End Using
    End Sub

    Public Sub Run(root As String, check As Action(Of Boolean, String))
        Dim folder = Path.Combine(root, "other-runtime")
        Directory.CreateDirectory(folder)
        WriteDll(Path.Combine(folder, "runtime32.dll"), &H14CUS) : WriteDll(Path.Combine(folder, "runtime64.dll"), &H8664US)
        Dim named = Path.Combine(folder, "example_openxr_32.json"), unnamed = Path.Combine(folder, "unnamed.json"), only64 = Path.Combine(folder, "example_openxr_64.json")
        SafeFiles.SaveJson(named, New With {.file_format_version = "1.0.0", .runtime = New With {.name = "Example OpenXR", .library_path = "runtime32.dll"}})
        SafeFiles.SaveJson(unnamed, New With {.runtime = New With {.library_path = "runtime32.dll"}})
        SafeFiles.SaveJson(only64, New With {.runtime = New With {.library_path = "runtime64.dll"}})

        ' The choice and its manifest.
        check(VrRuntime.Modes.SequenceEqual({"vdxr", "steamvr", "other"}), "VDXR, SteamVR and Other OpenXR runtime")
        Dim resolved = VrRuntime.Resolve("other", "", "", named)
        check(resolved.Mode = "other" AndAlso resolved.ManifestPath = named AndAlso resolved.Name = "Example OpenXR", "any manifest name works and the runtime's own name is shown")
        check(VrRuntime.Resolve("other", "", "", unnamed).Name = VrRuntime.OtherName, "without a name it is the Other OpenXR runtime")
        check(Refused(Sub() VrRuntime.Resolve("other", "", "", only64)), "a 64-bit-only runtime is refused (DiRT 3 is 32-bit)")
        check(Refused(Sub() VrRuntime.Resolve("other", named, named, "")) AndAlso Refused(Sub() VrRuntime.Resolve("other", "", "", Path.Combine(folder, "missing.json"))) AndAlso
              Refused(Sub() VrRuntime.Resolve("other", "", "", Path.Combine(folder, "runtime32.dll"))), "no manifest, a missing one or a non-.json file is refused, with no fallback to VDXR or SteamVR")
        check(Refused(Sub() VrRuntime.Resolve("openvr", "", "", named)), "other modes are still refused")

        ' Settings and profiles: the path stays with this PC.
        Dim settings As New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = "", .RuntimeMode = "other", .OtherRuntimeManifest = named}
        settings.Validate()
        check(Refused(Sub() Call New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = "", .RuntimeMode = "openvr"}.Validate()), "an unknown runtime choice is refused")
        Dim local As New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = "", .OtherRuntimeManifest = "C:\here\runtime.json"}
        Dim applied = ProfileValues.Capture(settings).ApplyTo(local)
        check(applied.RuntimeMode = "other" AndAlso applied.OtherRuntimeManifest = "C:\here\runtime.json", "a profile carries the choice but keeps this PC's manifest path")
        check(LauncherDefaults.All(settings).OtherRuntimeManifest = named AndAlso LauncherDefaults.All(settings).RuntimeMode = "vdxr", "Restore launcher defaults keeps the path and goes back to VDXR")

        ' The headset check names whatever runtime answers.
        Dim report = JsonSerializer.SerializeToElement(New With {.success = True, .stereo = False, .sessionCreated = False, .runtimeName = "Example OpenXR", .systemName = "Example headset", .views = {
            New With {.width = 2000, .height = 2100, .maxWidth = 4096, .maxHeight = 4096},
            New With {.width = 2000, .height = 2100, .maxWidth = 4096, .maxHeight = 4096}}})
        Dim headset = HeadsetResolution.FromReport(report, "other")
        check(headset.RuntimeMode = "other" AndAlso headset.Width = 2000 AndAlso headset.Summary(100).StartsWith("Example headset · Other OpenXR runtime", StringComparison.Ordinal), "the other runtime's headset is read and named")
        Dim nameless = JsonSerializer.SerializeToElement(New With {.success = True, .stereo = False, .sessionCreated = False, .runtimeName = "", .systemName = "Example headset", .views = {
            New With {.width = 2000, .height = 2100, .maxWidth = 4096, .maxHeight = 4096},
            New With {.width = 2000, .height = 2100, .maxWidth = 4096, .maxHeight = 4096}}})
        check(Refused(Sub() HeadsetResolution.FromReport(nameless, "other")), "a report without a runtime name is refused")

        ' A package that doesn't list it says so.
        Dim game = Path.Combine(folder, "game")
        Directory.CreateDirectory(game)
        Dim ctx As New InstallContext(game, Path.Combine(folder, "users"), Path.Combine(game, "graphics.xml"), folder)
        SafeFiles.SaveJson(Path.Combine(folder, "runtime-capabilities.json"), New With {.schema = 1, .supportedExeSha256 = InstallContext.SupportedHash, .experimentalInteractive = True, .runtimes = {"vdxr", "steamvr"}})
        settings.ExperimentalVrEnabled = True
        check(RuntimeCapabilities.ExperimentalReason(ctx, settings).Contains("the other OpenXR runtime"), "a package without it names the other runtime")
        SafeFiles.SaveJson(Path.Combine(folder, "runtime-capabilities.json"), New With {.schema = 1, .supportedExeSha256 = InstallContext.SupportedHash, .experimentalInteractive = True, .runtimes = {"vdxr", "steamvr", "other"}})
        check(RuntimeCapabilities.ExperimentalReason(ctx, settings) = "", "this build's packages list it")
    End Sub

    Public Sub RunUi(root As String, check As Action(Of Boolean, String))
        Dim game = Path.Combine(root, "other-runtime-ui")
        Directory.CreateDirectory(game)
        Dim ctx As New InstallContext(game, Path.Combine(root, "other-runtime-ui-users"), Path.Combine(game, "graphics.xml"), root)
        Call New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = ""}.Save(ctx)
        Using form As New MainForm(ctx)
            form.ShowInTaskbar = False : form.StartPosition = FormStartPosition.Manual : form.Location = New Drawing.Point(-30000, -30000)
            form.Show() : Application.DoEvents()
            Dim choice = DirectCast(form.Controls.Find("RuntimeChoice", True).Single(), ComboBox)
            Dim path = DirectCast(form.Controls.Find("OtherRuntimePath", True).Single(), TextBox)
            check(choice.Items.Count = 3 AndAlso choice.Items(2).ToString() = VrRuntime.OtherName AndAlso Not path.Enabled, "the runtime list has Other OpenXR runtime; its path waits for it")
            choice.SelectedIndex = 2 : path.Text = "C:\Runtimes\example_openxr_32.json" : Application.DoEvents()
            check(path.Enabled AndAlso Not DirectCast(form.Controls.Find("VdxrRuntimePath", True).Single(), TextBox).Enabled AndAlso
                  form.CurrentSettings().RuntimeMode = "other" AndAlso form.CurrentSettings().OtherRuntimeManifest = "C:\Runtimes\example_openxr_32.json", "choosing it enables its path and selects it")
            DirectCast(form.Controls.Find("SaveSettings", True).Single(), Button).PerformClick()
            Dim saved = LauncherSettings.Load(ctx)
            check(saved.RuntimeMode = "other" AndAlso saved.OtherRuntimeManifest = "C:\Runtimes\example_openxr_32.json", "Save settings keeps both")
            form.Close()
        End Using
        Using form As New MainForm(ctx)
            form.ShowInTaskbar = False : form.StartPosition = FormStartPosition.Manual : form.Location = New Drawing.Point(-30000, -30000)
            form.Show() : Application.DoEvents()
            check(DirectCast(form.Controls.Find("RuntimeChoice", True).Single(), ComboBox).SelectedIndex = 2 AndAlso DirectCast(form.Controls.Find("OtherRuntimePath", True).Single(), TextBox).Text = "C:\Runtimes\example_openxr_32.json",
                  "the launcher opens with it")
            form.Close()
        End Using
    End Sub
End Module
