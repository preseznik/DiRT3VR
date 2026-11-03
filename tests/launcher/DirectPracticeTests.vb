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
        check(Refused(Sub() transaction.Prepare(New Dictionary(Of String, Byte()) From {{"direct", changes("direct")}})) AndAlso Not transaction.Pending, "a direct practice config never goes into the game folder without DiRT3VR's renderer")

        Dim headset As New HeadsetResolution("vdxr", "Test headset", 2400, 2600)
        Dim runtime As New RuntimeInfo With {.Mode = "vdxr", .ManifestPath = "C:\runtime\virtualdesktop-openxr-32.json"}
        Dim logs = Path.Combine(ctx.UserRoot, "logs", "fixture")
        Dim directStart = InteractiveSession.StartInfo(ctx, runtime, logs, settings, headset)
        Dim normalStart = InteractiveSession.StartInfo(ctx, runtime, logs, New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = ""}, headset)
        check(directStart.ArgumentList.SequenceEqual({"-benchmark", "dirt3vr-direct.xml"}) AndAlso directStart.Environment("DIRT3VR_DIRECT_PRACTICE") = "1", "Launch VR in direct practice starts the benchmark with the player-control switch")
        check(directStart.Environment("DIRT3VR_DIRECT_LAPS") = "1", "a circuit gets its lap count (1 by default)")
        settings.DirectLaps = 3
        check(InteractiveSession.StartInfo(ctx, runtime, logs, settings, headset).Environment("DIRT3VR_DIRECT_LAPS") = "3", "the chosen lap count reaches the renderer")
        Dim stage As New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = "", .LaunchMode = "direct", .DirectRoute = catalog.RoutesFor("rally").First().Id, .DirectCar = "frx", .DirectLaps = 3}
        check(Not InteractiveSession.StartInfo(ctx, runtime, logs, stage, headset).Environment.ContainsKey("DIRT3VR_DIRECT_LAPS") AndAlso DirectPractice.Laps(stage) = 0, "point-to-point stages get no lap count")
        check(Refused(Sub() Call New LauncherSettings With {.DirectLaps = 6}.Validate()) AndAlso Refused(Sub() Call New LauncherSettings With {.DirectLaps = 0}.Validate()), "laps are 1 to 5")
        check(normalStart.ArgumentList.Count = 0 AndAlso Not normalStart.Environment.ContainsKey("DIRT3VR_DIRECT_PRACTICE"), "a normal launch is unchanged")

        ' Launch (desktop): the renderer as a plain pass-through with the player-control fix, journaled with
        ' the config as a schema 5 journal; no graphics or restrictions changes, no XR.
        Dim desktopCtx = Context(root, "desktop")
        check(DesktopStartupSession.NeedsSession(settings) AndAlso Not DesktopStartupSession.NeedsSession(New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = ""}), "desktop direct practice uses the managed desktop session; a plain launch doesn't")
        check(Refused(Sub() Session.LaunchDesktop(desktopCtx, settings)), "a plain desktop launch can't start direct practice")
        Dim desktopChanges = DesktopStartupSession.Changes(desktopCtx, settings)
        check(desktopChanges.Keys.Order().SequenceEqual({"direct", "proxy"}) AndAlso desktopChanges("proxy").SequenceEqual(File.ReadAllBytes(Path.Combine(desktopCtx.PackageRoot, "payload", "d3d11.dll"))), "desktop direct practice changes only its config and DiRT3VR's renderer")
        Dim desktopTransaction As New OwnedFileTransaction(desktopCtx)
        Dim originalGraphics = File.ReadAllBytes(desktopCtx.GraphicsPath)
        For Each extra In {"graphics", "restrictions"}
            Dim widened = New Dictionary(Of String, Byte())(desktopChanges)
            widened.Add(extra, changes(extra))
            check(Refused(Sub() desktopTransaction.Prepare(widened, apply:=False)) AndAlso Not desktopTransaction.Pending, "a desktop direct practice session refuses VR's " & extra & " change")
        Next
        desktopTransaction.Prepare(desktopChanges, apply:=False)
        check(SafeFiles.ReadJson(Of RecoveryJournal)(desktopTransaction.JournalPath).Schema = 5 AndAlso Not File.Exists(DirectPractice.ConfigPath(desktopCtx)), "desktop direct practice writes a schema 5 journal before anything is deployed")
        ' Deployed as the elevated worker would (ApplyPrepared needs the real game executable).
        File.WriteAllBytes(DirectPractice.ConfigPath(desktopCtx), desktopChanges("direct"))
        File.WriteAllBytes(Path.Combine(desktopCtx.GameRoot, "d3d11.dll"), desktopChanges("proxy"))
        Dim journalText = File.ReadAllText(desktopTransaction.JournalPath)
        Dim relabelled = System.Text.RegularExpressions.Regex.Replace(journalText, """Schema"":\s*5", """Schema"": 4")
        check(relabelled <> journalText, "the test relabels the journal")
        File.WriteAllText(desktopTransaction.JournalPath, relabelled)
        check(Refused(Sub() desktopTransaction.Recover()) AndAlso File.Exists(DirectPractice.ConfigPath(desktopCtx)), "a desktop journal relabelled as a VR one is refused and its files preserved")
        File.WriteAllText(desktopTransaction.JournalPath, journalText)
        check(desktopTransaction.Recover() AndAlso Not File.Exists(DirectPractice.ConfigPath(desktopCtx)) AndAlso Not File.Exists(Path.Combine(desktopCtx.GameRoot, "d3d11.dll")) AndAlso File.ReadAllBytes(desktopCtx.GraphicsPath).SequenceEqual(originalGraphics), "recovery removes the config and the renderer and leaves the player's graphics file alone")
        Dim desktopLogs = Path.Combine(desktopCtx.UserRoot, "logs", "session-fixture")
        Dim desktopStart = DesktopStartupSession.StartInfo(desktopCtx, settings, desktopLogs)
        Dim dirt3vrVariables = desktopStart.Environment.Keys.Where(Function(key) key.StartsWith("DIRT3VR_", StringComparison.OrdinalIgnoreCase)).Order().ToArray()
        check(desktopStart.ArgumentList.SequenceEqual({"-benchmark", "dirt3vr-direct.xml"}) AndAlso dirt3vrVariables.SequenceEqual({"DIRT3VR_DIAGNOSTICS", "DIRT3VR_DIRECT_LAPS", "DIRT3VR_DIRECT_PRACTICE", "DIRT3VR_LOG_DIR"}) AndAlso
              desktopStart.Environment("DIRT3VR_DIRECT_PRACTICE") = "1" AndAlso desktopStart.Environment("DIRT3VR_DIRECT_LAPS") = "3" AndAlso desktopStart.Environment("DIRT3VR_DIAGNOSTICS") = "1" AndAlso desktopStart.Environment("DIRT3VR_LOG_DIR") = desktopLogs AndAlso Not desktopStart.Environment.ContainsKey("XR_RUNTIME_JSON"),
              "desktop direct practice starts the benchmark with the player-control switch, laps and the observer log, and nothing for VR (" & String.Join(", ", dirt3vrVariables) & ")")
        check(Not DesktopStartupSession.StartInfo(desktopCtx, stage, desktopLogs).Environment.ContainsKey("DIRT3VR_DIRECT_LAPS"), "a desktop stage gets no lap count")
        check(DesktopStartupSession.StartInfo(desktopCtx, settings, desktopLogs, {"Logitech G HUB G920 Driving Force Racing Wheel USB", "xinput_pad"}).Environment("DIRT3VR_DIRECT_DEVICES") = "Logitech G HUB G920 Driving Force Racing Wheel USB|xinput_pad",
              "the player's devices are passed best first, for DiRT 3 to bind the right one (#16fa2aa3)")
        Environment.SetEnvironmentVariable("DIRT3VR_DIRECT_DEVICES", "stale")
        Try
            check(Not DesktopStartupSession.StartInfo(desktopCtx, settings, desktopLogs).Environment.ContainsKey("DIRT3VR_DIRECT_DEVICES"), "without bound devices nothing is passed, even if inherited")
        Finally
            Environment.SetEnvironmentVariable("DIRT3VR_DIRECT_DEVICES", Nothing)
        End Try
        Dim plainStart = DesktopStartupSession.StartInfo(desktopCtx, New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = "", .SkipStartupMovies = True}, desktopLogs)
        check(plainStart.ArgumentList.Count = 0 AndAlso Not plainStart.Environment.Keys.Any(Function(key) key.StartsWith("DIRT3VR_", StringComparison.OrdinalIgnoreCase)), "a startup-skip desktop session starts DiRT 3 as before")
        check(Refused(Sub() DesktopStartupSession.StartInfo(desktopCtx, settings, "logs")), "the desktop session log path must be absolute")

        ' Opponents on circuits: the player's car first, then the field (Wrench's runs 24-28).
        Dim race As New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = "", .LaunchMode = "direct", .DirectRoute = Aspen().Id, .DirectCar = "frx", .DirectOpponents = 3}
        race.Validate()
        Dim rallycross = catalog.CarsFor("rallycross", False, "", "").ToDictionary(Function(item) item.Code)
        Dim field = DirectPractice.OpponentField(race)
        check(race.DirectOpponentCars = "class" AndAlso field.Count = 3 AndAlso field.Sum(Function(entry) entry.Count) = 3 AndAlso
              field.All(Function(entry) entry.Code <> "frx" AndAlso rallycross.ContainsKey(entry.Code) AndAlso rallycross(entry.Code).VehicleClass = rallycross("frx").VehicleClass),
              "three opponents of the player's class (the default): three different cars, never the player's model")
        Dim raceConfig = Encoding.UTF8.GetString(DirectPractice.Config(race))
        Dim opponentCars = String.Concat(field.Select(Function(entry) "<car name=""" & entry.Code & """ number=""" & entry.Count & """ />"))
        check(raceConfig.Contains("<track country=""usa"" name=""aspen"" route=""route_0""><car name=""frx"" number=""1"" />" & opponentCars & "</track>"), "the player's car comes first, each opponent model after it (" & raceConfig & ")")
        DirectPractice.Validate(DirectPractice.Config(race))
        race.DirectOpponents = 7 : race.DirectOpponentCars = "mixed"
        check(DirectPractice.OpponentField(race).Count = 7 AndAlso DirectPractice.OpponentField(race).All(Function(entry) entry.Code <> "frx" AndAlso entry.Count = 1), "seven mixed opponents are seven other cars of the discipline")
        DirectPractice.Validate(DirectPractice.Config(race))
        race.DirectOpponentCars = "same"
        check(Encoding.UTF8.GetString(DirectPractice.Config(race)).Contains("<car name=""frx"" number=""8"" /></track>"), "My car's model shares the player's entry: number 8 is the player and 7 opponents")
        DirectPractice.Validate(DirectPractice.Config(race))
        Dim landRush = catalog.RoutesFor("landrush").First()
        Dim trucks = catalog.CarsFor("landrush", False, "", "").ToArray()
        Dim rush As New LauncherSettings With {.LaunchMode = "direct", .DirectRoute = landRush.Id, .DirectCar = trucks(0).Code, .DirectOpponents = 5, .DirectOpponentCars = "mixed"}
        check(trucks.Length = 2 AndAlso DirectPractice.OpponentField(rush).Single().Code = trucks(1).Code AndAlso DirectPractice.OpponentField(rush).Single().Count = 5, "Land Rush's mixed field repeats the other vehicle")
        rush.DirectOpponentCars = "class"
        check(DirectPractice.OpponentField(rush).Single().Code = trucks(0).Code, "with no other car in the class, opponents drive the player's model")
        Dim solo = New LauncherSettings With {.LaunchMode = "direct", .DirectRoute = stage.DirectRoute, .DirectCar = "frx", .DirectOpponents = 4}
        check(DirectPractice.Opponents(solo) = 0 AndAlso DirectPractice.OpponentField(solo).Count = 0 AndAlso Encoding.UTF8.GetString(DirectPractice.Config(solo)).Split("<car ").Length = 2, "stages stay solo whatever the opponents setting")
        Dim soloConfig = Encoding.UTF8.GetString(DirectPractice.Config(solo))
        Dim refusedConfig = Function(text As String) Refused(Sub() DirectPractice.Validate(Encoding.UTF8.GetBytes(text)))
        Dim otherCar = rallycross.Keys.First(Function(code) code <> "frx")
        check(refusedConfig(config.Replace("number=""1""", "number=""9""")) AndAlso refusedConfig(config.Replace("number=""1""", "number=""0""")) AndAlso refusedConfig(config.Replace("number=""1""", "number=""01""")) AndAlso
              refusedConfig(config.Replace("</track>", "<car name=""zzz"" number=""1"" /></track>")) AndAlso refusedConfig(config.Replace("</track>", "<car name=""frx"" number=""2"" /></track>")) AndAlso
              refusedConfig(soloConfig.Replace("</track>", "<car name=""" & otherCar & """ number=""1"" /></track>")) AndAlso refusedConfig(soloConfig.Replace("number=""1""", "number=""2""")) AndAlso
              refusedConfig(raceConfig.Replace(opponentCars, opponentCars & "<car name=""" & otherCar & """ number=""5"" />")),
              "the journal check refuses more than 8 cars, counts that aren't plain numbers, unknown cars, the player's model listed twice and opponents on a stage")
        check(Refused(Sub() Call New LauncherSettings With {.DirectOpponents = 8}.Validate()) AndAlso Refused(Sub() Call New LauncherSettings With {.DirectOpponents = -1}.Validate()) AndAlso
              Refused(Sub() Call New LauncherSettings With {.DirectOpponentCars = "ai"}.Validate()), "opponents are 0 to 7 with a known car choice")
        Dim opponentsCtx = Context(root, "opponents")
        Dim opponentsTransaction As New OwnedFileTransaction(opponentsCtx)
        race.DirectOpponentCars = "class" : race.DirectOpponents = 3
        opponentsTransaction.Prepare(DesktopStartupSession.Changes(opponentsCtx, race), apply:=False)
        check(opponentsTransaction.Pending AndAlso opponentsTransaction.Recover() AndAlso Not opponentsTransaction.Pending, "a config with opponents passes the recovery journal's check")

        ' AI difficulty (Wrench's 7409e13): sent only while opponents race, unset = DiRT 3's own setting.
        check(race.DirectDifficulty = -1 AndAlso Not InteractiveSession.StartInfo(ctx, runtime, logs, race, headset).Environment.ContainsKey("DIRT3VR_DIRECT_DIFFICULTY"), "by default the AI keeps the player's own DiRT 3 difficulty")
        race.DirectDifficulty = 4
        check(InteractiveSession.StartInfo(ctx, runtime, logs, race, headset).Environment("DIRT3VR_DIRECT_DIFFICULTY") = "4" AndAlso
              DesktopStartupSession.StartInfo(opponentsCtx, race, Path.Combine(opponentsCtx.UserRoot, "logs", "session-fixture")).Environment("DIRT3VR_DIRECT_DIFFICULTY") = "4", "a chosen AI level reaches the renderer with Launch VR and Launch")
        race.DirectOpponents = 0
        check(Not InteractiveSession.StartInfo(ctx, runtime, logs, race, headset).Environment.ContainsKey("DIRT3VR_DIRECT_DIFFICULTY"), "without opponents the AI level isn't sent")
        Dim stageRace As New LauncherSettings With {.LaunchMode = "direct", .DirectRoute = stage.DirectRoute, .DirectCar = "frx", .DirectOpponents = 3, .DirectDifficulty = 2}
        check(DirectPractice.Difficulty(stageRace) = -1 AndAlso Not InteractiveSession.StartInfo(ctx, runtime, logs, stageRace, headset).Environment.ContainsKey("DIRT3VR_DIRECT_DIFFICULTY"), "stages, which have no opponents, get no AI level")
        check(Refused(Sub() Call New LauncherSettings With {.DirectDifficulty = 6}.Validate()) AndAlso Refused(Sub() Call New LauncherSettings With {.DirectDifficulty = -2}.Validate()), "the AI level is DiRT 3's 0 to 5, or the game's own setting")

        ' After the finish (Wrench's 05e63da): DiRT 3 quits by default, or goes to its main menu.
        check(race.DirectAfterFinish = "quit" AndAlso Not InteractiveSession.StartInfo(ctx, runtime, logs, race, headset).Environment.ContainsKey("DIRT3VR_DIRECT_RETURN"), "by default DiRT 3 quits after the finish, as its benchmark start does")
        race.DirectAfterFinish = "menus"
        check(InteractiveSession.StartInfo(ctx, runtime, logs, race, headset).Environment("DIRT3VR_DIRECT_RETURN") = "1" AndAlso
              DesktopStartupSession.StartInfo(opponentsCtx, race, Path.Combine(opponentsCtx.UserRoot, "logs", "session-fixture")).Environment("DIRT3VR_DIRECT_RETURN") = "1", "Return to DiRT 3's menus reaches the renderer with Launch VR and Launch")
        check(Refused(Sub() Call New LauncherSettings With {.DirectAfterFinish = "results"}.Validate()), "after the finish is Quit to desktop or Return to DiRT 3's menus")

        ' Assists and gearbox (Wrench's ac09f6f): only changes from DiRT 3's defaults reach the renderer.
        Dim assisted As New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = "", .LaunchMode = "direct", .DirectRoute = Aspen().Id, .DirectCar = "frx"}
        check(DirectPractice.Assists(assisted) = "" AndAlso Not InteractiveSession.StartInfo(ctx, runtime, logs, assisted, headset).Environment.ContainsKey("DIRT3VR_DIRECT_ASSISTS"), "DiRT 3's default assists (ABS on, automatic) send nothing")
        assisted.DirectAbs = False : assisted.DirectGearbox = "clutch" : assisted.DirectStability = True : assisted.DirectCornerBraking = True : assisted.DirectThrottle = True : assisted.DirectSteering = True
        Dim everything = "assists_abs=0,assists_stability=1,assists_corner_braking=1,assists_throttle_mngt=1,assists_auto_steer=1,transmission_type=3"
        check(DirectPractice.Assists(assisted) = everything AndAlso InteractiveSession.StartInfo(ctx, runtime, logs, assisted, headset).Environment("DIRT3VR_DIRECT_ASSISTS") = everything AndAlso
              DesktopStartupSession.StartInfo(opponentsCtx, assisted, Path.Combine(opponentsCtx.UserRoot, "logs", "session-fixture")).Environment("DIRT3VR_DIRECT_ASSISTS") = everything,
              "every changed assist and the gearbox reach the renderer, with Launch VR and Launch (" & DirectPractice.Assists(assisted) & ")")
        check(DirectPractice.Assists(New LauncherSettings With {.DirectGearbox = "sequential"}) = "transmission_type=1" AndAlso DirectPractice.Assists(New LauncherSettings With {.DirectGearbox = "hpattern"}) = "transmission_type=2",
              "the gearbox maps to DiRT 3's transmission types")
        check(Refused(Sub() Call New LauncherSettings With {.DirectGearbox = "paddles"}.Validate()), "an unknown gearbox is refused")

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
        check(loaded.LaunchMode = "direct" AndAlso loaded.DirectRoute = Aspen().Id AndAlso loaded.DirectCar = "frx" AndAlso loaded.DirectLaps = 3, "the direct practice choice is saved with the launcher settings")
        Dim profile = ProfileValues.Capture(loaded)
        check(profile.ApplyTo(New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = ""}).DirectCar = "frx" AndAlso profile.DirectLaps = 3, "settings profiles carry the direct practice choice")
        check(Refused(Sub() Call New SettingsProfile With {.Kind = "DiRT3VR.SettingsProfile", .Schema = 3, .Name = "Old", .Settings = profile}.Validate()), "a schema 3 profile can't carry direct practice")
        loaded.DirectOpponents = 4 : loaded.DirectOpponentCars = "mixed"
        Dim raceProfile = ProfileValues.Capture(loaded).ApplyTo(New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = ""})
        loaded.DirectDifficulty = 5 : loaded.DirectAfterFinish = "menus" : loaded.DirectGearbox = "hpattern" : loaded.DirectAbs = False : loaded.DirectThrottle = True
        raceProfile = ProfileValues.Capture(loaded).ApplyTo(New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = ""})
        check(raceProfile.DirectOpponents = 4 AndAlso raceProfile.DirectOpponentCars = "mixed" AndAlso raceProfile.DirectDifficulty = 5 AndAlso raceProfile.DirectAfterFinish = "menus" AndAlso raceProfile.DirectGearbox = "hpattern" AndAlso Not raceProfile.DirectAbs AndAlso raceProfile.DirectThrottle,
              "settings profiles carry the opponents, their AI level, what happens after the finish, the gearbox and the assists")
        check(Refused(Sub() Call New SettingsProfile With {.Kind = "DiRT3VR.SettingsProfile", .Schema = 3, .Name = "Old", .Settings = New ProfileValues With {.DirectAbs = False}}.Validate()), "a schema 3 profile can't carry assists")
        check(Refused(Sub() Call New SettingsProfile With {.Kind = "DiRT3VR.SettingsProfile", .Schema = 3, .Name = "Old", .Settings = New ProfileValues With {.DirectAfterFinish = "menus"}}.Validate()), "a schema 3 profile can't return to the menus")
        check(Refused(Sub() Call New SettingsProfile With {.Kind = "DiRT3VR.SettingsProfile", .Schema = 3, .Name = "Old", .Settings = New ProfileValues With {.DirectDifficulty = 1}}.Validate()), "a schema 3 profile can't carry an AI level")
        check(Refused(Sub() Call New SettingsProfile With {.Kind = "DiRT3VR.SettingsProfile", .Schema = 3, .Name = "Old", .Settings = New ProfileValues With {.DirectOpponents = 2}}.Validate()), "a schema 3 profile can't carry opponents")
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
            check(normal.Checked AndAlso Not discipline.Enabled AndAlso Not car.Enabled AndAlso Not find("DirectAfterFinish").Enabled AndAlso desktop.Enabled AndAlso form.CurrentSettings().LaunchMode = "normal" AndAlso Not form.HasUnsavedSettings(), "Normal launch is the default and the picker is off")
            direct.Checked = True : Application.DoEvents()
            check(discipline.Enabled AndAlso route.Items.Count > 0 AndAlso car.Items.Count > 0 AndAlso form.CurrentSettings().LaunchMode = "direct" AndAlso form.HasUnsavedSettings(), "choosing Direct practice turns the picker on")
            check(desktop.Enabled, "Launch stays available in direct practice (it starts on the monitor)")
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
            Dim lapSlider = DirectCast(find("DirectLaps"), ValueSlider)
            check(lapSlider.Enabled, "laps can be set for a circuit")
            lapSlider.Value = 3 : Application.DoEvents()
            check(find("DirectLapsValue").Text = "3 laps", "the laps slider reads 3 laps")
            Dim rivals = DirectCast(find("DirectOpponents"), ValueSlider), rivalCars = DirectCast(find("DirectOpponentCars"), ComboBox)
            Dim aiLevel = DirectCast(find("DirectDifficulty"), ComboBox)
            check(rivals.Enabled AndAlso rivals.Value = 0 AndAlso find("DirectOpponentsValue").Text = "None" AndAlso Not rivalCars.Enabled AndAlso Not aiLevel.Enabled AndAlso aiLevel.SelectedIndex = 0,
                  "a circuit can take opponents; their cars and AI level are chosen once there are some")
            rivals.Value = 3 : rivalCars.SelectedIndex = 1 : Application.DoEvents()
            check(aiLevel.Enabled AndAlso form.CurrentSettings().DirectDifficulty = -1 AndAlso Not find("DirectSummary").Text.Contains("AI level"), "with opponents the AI level starts on My DiRT 3 setting")
            aiLevel.SelectedIndex = 4 : Application.DoEvents()
            check(form.CurrentSettings().DirectDifficulty = 3 AndAlso find("DirectSummary").Text.Contains("(mixed cars, AI level 3)"), "a chosen AI level shows in the settings and the summary")
            Dim afterFinish = DirectCast(find("DirectAfterFinish"), ComboBox)
            check(afterFinish.Enabled AndAlso afterFinish.SelectedIndex = 0 AndAlso find("DirectSummary").Text.EndsWith("DiRT 3 closes by itself after the finish.", StringComparison.Ordinal), "after the finish starts on Quit to desktop")
            afterFinish.SelectedIndex = 1 : Application.DoEvents()
            check(form.CurrentSettings().DirectAfterFinish = "menus" AndAlso find("DirectSummary").Text.EndsWith("After the finish DiRT 3 returns to its menus.", StringComparison.Ordinal), "Return to DiRT 3's menus shows in the settings and the summary")
            Dim gearboxBox = DirectCast(find("DirectGearbox"), ComboBox), absBox = DirectCast(find("DirectAbs"), CheckBox)
            check(gearboxBox.Enabled AndAlso gearboxBox.SelectedIndex = 0 AndAlso absBox.Enabled AndAlso absBox.Checked AndAlso Not DirectCast(find("DirectStability"), CheckBox).Checked, "gearbox and assists start on DiRT 3's defaults")
            gearboxBox.SelectedIndex = 3 : absBox.Checked = False : Application.DoEvents()
            check(form.CurrentSettings().DirectGearbox = "clutch" AndAlso Not form.CurrentSettings().DirectAbs AndAlso find("DirectSummary").Text.Contains("· H-pattern gears with clutch."),
                  "the gearbox and assists show in the settings and the gearbox in the summary (" & find("DirectSummary").Text & ")")
            check(rivalCars.Enabled AndAlso find("DirectSummary").Text.Contains("· 3 opponents (mixed cars, AI level 3) ·") AndAlso form.CurrentSettings().DirectOpponents = 3 AndAlso form.CurrentSettings().DirectOpponentCars = "mixed",
                  "opponents show in the summary and the settings (" & find("DirectSummary").Text & ")")
            Dim summary = find("DirectSummary").Text
            check(summary.StartsWith("Direct practice: RALLYCROSS · ASPEN – LAKESIDE · 3 laps", StringComparison.Ordinal), "the summary names the discipline, route, laps and car (" & summary & ")")
            DirectCast(find("SaveSettings"), Button).PerformClick()
            Dim saved = LauncherSettings.Load(ctx)
            check(saved.LaunchMode = "direct" AndAlso saved.DirectRoute = Aspen().Id AndAlso saved.DirectCar = chosenCar AndAlso saved.DirectLaps = 3 AndAlso saved.DirectOpponents = 3 AndAlso saved.DirectOpponentCars = "mixed" AndAlso saved.DirectDifficulty = 3 AndAlso saved.DirectAfterFinish = "menus", "Save settings keeps the direct practice choice")
            discipline.SelectedItem = EventCatalog.Current.FindDiscipline("rally") : Application.DoEvents()
            check(Not lapSlider.Enabled AndAlso Not rivals.Enabled AndAlso Not rivalCars.Enabled AndAlso Not aiLevel.Enabled AndAlso Not find("DirectSummary").Text.Contains(" laps") AndAlso Not find("DirectSummary").Text.Contains("opponents ("), "point-to-point stages have no laps and no opponents")
            discipline.SelectedItem = EventCatalog.Current.FindDiscipline("rallycross") : Application.DoEvents()
            route.SelectedItem = route.Items.Cast(Of EventCatalog.EventRoute)().Single(Function(item) item.Track = "aspen" AndAlso item.Route = "route_0")
            car.SelectedItem = car.Items.Cast(Of EventCatalog.EventCar)().Single(Function(item) item.Code = chosenCar)
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
