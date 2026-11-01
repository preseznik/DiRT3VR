Imports DiRT3VR
Imports System.IO
Imports System.Text
Imports System.Windows.Forms

Module DirectPracticeTests
    ' A game folder plus a Documents-like hardwaresettings folder (so the benchmarks folder sits beside it).
    Private Function Context(root As String, name As String) As InstallContext
        Dim game = Path.Combine(root, "direct", name, "game")
        Dim documents = Path.Combine(root, "direct", name, "documents", "hardwaresettings")
        Directory.CreateDirectory(game) : Directory.CreateDirectory(documents)
        Dim ctx As New InstallContext(game, Path.Combine(root, "direct", name, "users"), Path.Combine(documents, "hardware_settings_config.xml"), Path.Combine(root, "direct", name, "package"))
        Directory.CreateDirectory(Path.Combine(game, "system")) : Directory.CreateDirectory(Path.Combine(ctx.PackageRoot, "payload"))
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "hardware_settings_restrictions.xml"), Path.Combine(game, "system", "hardware_settings_restrictions.xml"), True)
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "rewrite-2026-10-31", "original.xml"), ctx.GraphicsPath, True)
        File.WriteAllText(Path.Combine(ctx.PackageRoot, "payload", "d3d11.dll"), "owned test payload")
        Return ctx
    End Function

    Private Function Refused(action As Action) As Boolean
        Try
            action()
        Catch ex As IOException
            Return True
        End Try
        Return False
    End Function

    Private Function Aspen() As EventCatalog.EventRoute
        Return EventCatalog.Current.Routes.Single(Function(route) route.Track = "aspen" AndAlso route.Route = "route_0" AndAlso route.Discipline = "rallycross")
    End Function

    Public Sub Run(root As String, check As Action(Of Boolean, String))
        Dim catalog = EventCatalog.Current
        check(catalog.Disciplines.Select(Function(item) item.Id).SequenceEqual({"rally", "rallycross", "trailblazer", "landrush"}) AndAlso catalog.Routes.Count = 80 AndAlso catalog.Cars.Count = 55, "the embedded event catalog has the four benchmark disciplines, 80 routes and 55 cars")
        check(catalog.RoutesFor("rallycross").All(Function(route) route.Discipline = "rallycross") AndAlso catalog.CarsFor("landrush", False, "", "").All(Function(car) car.Discipline = "landrush"), "routes and cars follow the discipline")
        check(catalog.CarsFor("rally", True, "", "").Count() = 55 AndAlso catalog.CarsFor("rally", True, "", "subaru").All(Function(car) car.Name.Contains("SUBARU")) AndAlso catalog.CarsFor("rally", False, "GROUP B", "").All(Function(car) car.VehicleClassName = "GROUP B"),
              "the car list can show every discipline, search names and filter by class")
        Dim refusedCatalog = Refused(Sub() EventCatalog.Parse("{""schema"":1,""disciplines"":[{""id"":""rally""}],""routes"":[{""id"":1,""discipline"":""rally"",""country"":""usa"",""track"":""a\""b"",""route"":""route_0""}],""cars"":[{""code"":""frx"",""discipline"":""rally""}]}"))
        check(refusedCatalog, "catalog values that would go into the game's config must be plain identifiers")

        ' The config DiRT 3 reads: the route and car, the player's own graphics file and a known result prefix.
        Dim settings As New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = "", .LaunchMode = "direct", .DirectRoute = Aspen().Id, .DirectCar = "frx"}
        settings.Validate()
        Dim config = Encoding.UTF8.GetString(DirectPractice.Config(settings))
        check(config = "<?xml version=""1.0"" encoding=""utf-8""?><config skipreplays=""true"" outputfilename=""DiRT3VR_direct"" hardwaresettings=""hardware_settings_config.xml""><track country=""usa"" name=""aspen"" route=""route_0""><car name=""frx"" number=""1"" /></track></config>",
              "the direct practice config is the benchmark start ClaudeCode verified (" & config & ")")
        DirectPractice.Validate(DirectPractice.Config(settings))
        check(Refused(Sub() DirectPractice.Validate(Encoding.UTF8.GetBytes(config.Replace("route_0", "route_zz")))) AndAlso Refused(Sub() DirectPractice.Validate(Encoding.UTF8.GetBytes(config.Replace("skipreplays=""true""", "skipreplays=""false""")))) AndAlso
              Refused(Sub() DirectPractice.Validate(Encoding.UTF8.GetBytes("<config/>"))), "only exactly the launcher's own config is accepted")
        check(Refused(Sub() Call New LauncherSettings With {.LaunchMode = "direct"}.Validate()) AndAlso Refused(Sub() Call New LauncherSettings With {.DirectCar = "nope"}.Validate()) AndAlso Refused(Sub() Call New LauncherSettings With {.LaunchMode = "warp"}.Validate()),
              "direct practice needs a listed route and car")

        ' Launch VR: the config is journaled with the session's other changes and the game gets the start.
        Dim ctx = Context(root, "session")
        Dim changes = ExperimentalFiles.Changes(ctx, 1800, 1950).ToDictionary(Function(pair) pair.Key, Function(pair) pair.Value)
        changes.Add("direct", DirectPractice.Config(settings))
        Dim transaction As New OwnedFileTransaction(ctx)
        transaction.Prepare(changes, experimental:=True, apply:=False)
        check(SafeFiles.ReadJson(Of RecoveryJournal)(transaction.JournalPath).Schema = 4, "a session with direct practice writes a schema 4 journal (older launchers refuse it rather than leave the file behind)")
        For Each pair In changes
            Dim target = If(pair.Key = "direct", DirectPractice.ConfigPath(ctx), If(pair.Key = "proxy", Path.Combine(ctx.GameRoot, "d3d11.dll"), If(pair.Key = "graphics", ctx.GraphicsPath, Path.Combine(ctx.GameRoot, "system", "hardware_settings_restrictions.xml"))))
            File.WriteAllBytes(target, pair.Value)
        Next
        check(transaction.Recover() AndAlso Not File.Exists(DirectPractice.ConfigPath(ctx)) AndAlso Not File.Exists(Path.Combine(ctx.GameRoot, "d3d11.dll")), "recovery removes the direct practice config with the proxy")
        File.WriteAllText(DirectPractice.ConfigPath(ctx), "someone else's file")
        check(Refused(Sub() transaction.Prepare(changes, experimental:=True, apply:=False)) AndAlso File.ReadAllText(DirectPractice.ConfigPath(ctx)) = "someone else's file" AndAlso Not transaction.Pending, "an existing dirt3vr-direct.xml is preserved and the launch refused")
        File.Delete(DirectPractice.ConfigPath(ctx))
        Dim tampered = New Dictionary(Of String, Byte())(changes)
        tampered("direct") = Encoding.UTF8.GetBytes(config.Replace("frx", "zzz"))
        check(Refused(Sub() transaction.Prepare(tampered, experimental:=True, apply:=False)) AndAlso Not transaction.Pending, "a config that isn't the launcher's own is refused before anything is staged")
        check(Refused(Sub() transaction.Prepare(New Dictionary(Of String, Byte()) From {{"direct", changes("direct")}})), "direct practice belongs to a VR session")

        Dim headset As New HeadsetResolution("vdxr", "Test headset", 2400, 2600)
        Dim runtime As New RuntimeInfo With {.Mode = "vdxr", .ManifestPath = "C:\runtime\virtualdesktop-openxr-32.json"}
        Dim logs = Path.Combine(ctx.UserRoot, "logs", "fixture")
        Dim directStart = InteractiveSession.StartInfo(ctx, runtime, logs, settings, headset)
        Dim normalStart = InteractiveSession.StartInfo(ctx, runtime, logs, New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = ""}, headset)
        check(directStart.ArgumentList.SequenceEqual({"-benchmark", "dirt3vr-direct.xml"}) AndAlso directStart.Environment("DIRT3VR_DIRECT_PRACTICE") = "1", "Launch VR in direct practice starts the benchmark with the player-control switch")
        check(normalStart.ArgumentList.Count = 0 AndAlso Not normalStart.Environment.ContainsKey("DIRT3VR_DIRECT_PRACTICE"), "a normal launch is unchanged")

        ' DiRT 3's result file for the session is removed; other benchmark results stay.
        Dim results = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(ctx.GraphicsPath)), "benchmarks")
        Directory.CreateDirectory(results)
        File.WriteAllText(Path.Combine(results, "DiRT3VR_direct_2026-11-01_16-00-00.xml"), "<results/>")
        File.WriteAllText(Path.Combine(results, "my_benchmark.xml"), "<results/>")
        DirectPractice.RemoveResults(ctx)
        check(Not File.Exists(Path.Combine(results, "DiRT3VR_direct_2026-11-01_16-00-00.xml")) AndAlso File.Exists(Path.Combine(results, "my_benchmark.xml")), "only direct practice's own benchmark results are removed")

        ' Settings and profiles (schema 4).
        settings.Save(ctx)
        Dim loaded = LauncherSettings.Load(ctx)
        check(loaded.LaunchMode = "direct" AndAlso loaded.DirectRoute = Aspen().Id AndAlso loaded.DirectCar = "frx", "the direct practice choice is saved with the launcher settings")
        Dim profile = ProfileValues.Capture(loaded)
        check(profile.ApplyTo(New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = ""}).DirectCar = "frx", "settings profiles carry the direct practice choice")
        check(Refused(Sub() Call New SettingsProfile With {.Kind = "DiRT3VR.SettingsProfile", .Schema = 3, .Name = "Old", .Settings = profile}.Validate()), "a schema 3 profile can't carry direct practice")
        check(LauncherDefaults.All(loaded).LaunchMode = "normal", "Restore launcher defaults goes back to Normal launch")
    End Sub

    Public Sub RunUi(root As String, check As Action(Of Boolean, String))
        Dim ctx = Context(root, "ui")
        Call New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = ""}.Save(ctx)
        Using form As New MainForm(ctx)
            form.ShowInTaskbar = False : form.StartPosition = FormStartPosition.Manual : form.Location = New Drawing.Point(-30000, -30000)
            form.Show() : Application.DoEvents()
            Dim find = Function(name As String) form.Controls.Find(name, True).Single()
            Dim normal = DirectCast(find("LaunchModeNormal"), RadioButton), direct = DirectCast(find("LaunchModeDirect"), RadioButton)
            Dim discipline = DirectCast(find("DirectDiscipline"), ComboBox), route = DirectCast(find("DirectRoute"), ComboBox), car = DirectCast(find("DirectCar"), ComboBox)
            Dim search = DirectCast(find("DirectCarSearch"), TextBox), allCars = DirectCast(find("DirectAllCars"), CheckBox), carClass = DirectCast(find("DirectCarClass"), ComboBox)
            Dim desktop = find("LaunchDesktop")
            check(normal.Checked AndAlso Not discipline.Enabled AndAlso Not car.Enabled AndAlso desktop.Enabled AndAlso form.CurrentSettings().LaunchMode = "normal" AndAlso Not form.HasUnsavedSettings(), "Normal launch is the default and the picker is off")
            direct.Checked = True : Application.DoEvents()
            check(discipline.Enabled AndAlso route.Items.Count > 0 AndAlso car.Items.Count > 0 AndAlso form.CurrentSettings().LaunchMode = "direct" AndAlso form.HasUnsavedSettings(), "choosing Direct practice turns the picker on")
            check(Not desktop.Enabled AndAlso desktop.AccessibleDescription.Contains("Launch VR"), "desktop Launch explains that direct practice starts with Launch VR for now")
            discipline.SelectedItem = EventCatalog.Current.FindDiscipline("rallycross") : Application.DoEvents()
            check(route.Items.Cast(Of EventCatalog.EventRoute)().All(Function(item) item.Discipline = "rallycross") AndAlso car.Items.Cast(Of EventCatalog.EventCar)().All(Function(item) item.Discipline = "rallycross"), "routes and cars follow the discipline")
            Dim rallycrossCars = car.Items.Count
            allCars.Checked = True : Application.DoEvents()
            check(car.Items.Count = 55, "Show cars from other disciplines lists every car")
            search.Text = "subaru" : Application.DoEvents()
            check(car.Items.Count > 0 AndAlso car.Items.Cast(Of EventCatalog.EventCar)().All(Function(item) item.Name.Contains("SUBARU")), "search narrows the cars")
            search.Text = "" : allCars.Checked = False : Application.DoEvents()
            check(car.Items.Count = rallycrossCars AndAlso carClass.Items(0).ToString() = "All classes", "clearing the filters brings the discipline's cars back")
            route.SelectedItem = route.Items.Cast(Of EventCatalog.EventRoute)().Single(Function(item) item.Track = "aspen" AndAlso item.Route = "route_0")
            car.SelectedItem = car.Items.Cast(Of EventCatalog.EventCar)().First()
            Dim chosenCar = DirectCast(car.SelectedItem, EventCatalog.EventCar).Code
            Dim summary = find("DirectSummary").Text
            check(summary.StartsWith("Direct practice: RALLYCROSS · ASPEN – LAKESIDE", StringComparison.Ordinal), "the summary names the discipline, route and car (" & summary & ")")
            DirectCast(find("SaveSettings"), Button).PerformClick()
            Dim saved = LauncherSettings.Load(ctx)
            check(saved.LaunchMode = "direct" AndAlso saved.DirectRoute = Aspen().Id AndAlso saved.DirectCar = chosenCar, "Save settings keeps the direct practice choice")
            form.Close()
        End Using
        Using form As New MainForm(ctx)
            form.ShowInTaskbar = False : form.StartPosition = FormStartPosition.Manual : form.Location = New Drawing.Point(-30000, -30000)
            form.Show() : Application.DoEvents()
            check(DirectCast(form.Controls.Find("LaunchModeDirect", True).Single(), RadioButton).Checked AndAlso DirectCast(form.Controls.Find("DirectRoute", True).Single(), ComboBox).Text.StartsWith("ASPEN – LAKESIDE", StringComparison.Ordinal) AndAlso Not form.HasUnsavedSettings(),
                  "reopening the launcher shows the saved direct practice choice")
            form.Close()
        End Using
    End Sub
End Module
