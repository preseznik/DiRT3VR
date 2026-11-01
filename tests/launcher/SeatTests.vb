Imports DiRT3VR
Imports System.IO
Imports System.Windows.Forms

Module SeatTests
    Private Function Fixture(name As String) As Byte()
        Return File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "seat", name))
    End Function

    ' A game folder with VR's files and the synthetic camera file for two catalog cars.
    Private Function Context(root As String, name As String) As InstallContext
        Dim game = Path.Combine(root, "seat", name, "game")
        Dim documents = Path.Combine(root, "seat", name, "documents", "hardwaresettings")
        Directory.CreateDirectory(game) : Directory.CreateDirectory(documents)
        Dim ctx As New InstallContext(game, Path.Combine(root, "seat", name, "users"), Path.Combine(documents, "hardware_settings_config.xml"), Path.Combine(root, "seat", name, "package"))
        Directory.CreateDirectory(Path.Combine(game, "system")) : Directory.CreateDirectory(Path.Combine(ctx.PackageRoot, "payload"))
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "hardware_settings_restrictions.xml"), Path.Combine(game, "system", "hardware_settings_restrictions.xml"), True)
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "rewrite-2026-10-31", "original.xml"), ctx.GraphicsPath, True)
        File.WriteAllText(Path.Combine(ctx.PackageRoot, "payload", "d3d11.dll"), "owned test payload")
        For Each code In {"frx", "stx"}
            Directory.CreateDirectory(Path.GetDirectoryName(SeatPositions.CameraPath(ctx, code)))
            File.WriteAllBytes(SeatPositions.CameraPath(ctx, code), Fixture("cameras.bin"))
        Next
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

    Public Sub Run(root As String, check As Action(Of Boolean, String))
        ' The camera file edit matches Wrench's tools/seat_cameras.py byte for byte (fixtures made by it).
        Dim original = Fixture("cameras.bin")
        Dim eye = SeatCameras.Eye(original)
        check(eye.X = 0.346 AndAlso eye.Y = 1.068 AndAlso eye.Z = -0.317, "the cockpit eye point is read from head-cam's position (" & eye.ToString() & ")")
        For Each expected In {("left-up-forward", New SeatOffset With {.Sideways = 10, .Up = 5, .Forward = 30}), ("limits", New SeatOffset With {.Sideways = -50, .Up = 50, .Forward = -50}), ("centimetre", New SeatOffset With {.Sideways = 1, .Up = -2, .Forward = 7})}
            check(SeatPositions.Apply(original, expected.Item2).SequenceEqual(Fixture("cameras-" & expected.Item1 & ".bin")), "the " & expected.Item1 & " offset gives exactly the reference tool's file")
        Next
        check(SeatPositions.Apply(original, New SeatOffset).SequenceEqual(original), "a zero offset gives back the original bytes")
        Dim moved = SeatCameras.Eye(SeatPositions.Apply(original, New SeatOffset With {.Sideways = 10, .Up = 5, .Forward = 30}))
        check(Math.Abs(moved.X - 0.446) < 0.00001 AndAlso Math.Abs(moved.Y - 1.118) < 0.00001 AndAlso Math.Abs(moved.Z + 0.017) < 0.00001, "the eye point moves 10 cm left, 5 cm up and 30 cm forward")
        check(Refused(Sub() SeatPositions.Apply(original, New SeatOffset With {.Up = 51})) AndAlso Refused(Sub() SeatCameras.Offset(original, Double.NaN, 0, 0)), "offsets beyond ±50 cm or not numbers are refused")
        Dim corrupt = CType(original.Clone(), Byte()) : corrupt(0) = 0
        Dim truncated = original.Take(original.Length - 8).ToArray()
        Dim noCockpit = CType(original.Clone(), Byte())
        Dim at = Text.Encoding.ASCII.GetString(noCockpit).IndexOf("head-cam" & ChrW(0), StringComparison.Ordinal)
        noCockpit(at + 5) = CByte(AscW("x"c))
        check(Refused(Sub() SeatCameras.Eye(corrupt)) AndAlso Refused(Sub() SeatCameras.Eye(truncated)) AndAlso Refused(Sub() SeatCameras.Eye(noCockpit)) AndAlso Refused(Sub() SeatCameras.Eye(Array.Empty(Of Byte)())),
              "a file that isn't a camera file, is cut short or has no cockpit view is refused")

        ' Settings: per car or one for every car; zero offsets aren't kept.
        Dim settings As New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = "", .SeatOffsets = New Dictionary(Of String, SeatOffset) From {{"stx", New SeatOffset With {.Up = 3}}, {"frx", New SeatOffset With {.Forward = -4}}, {"mex", New SeatOffset}}}
        settings.Validate()
        check(settings.SeatOffsets.Keys.SequenceEqual({"frx", "stx"}), "settings keep only adjusted cars, in code order")
        check(Refused(Sub() Call New LauncherSettings With {.SeatOffsets = New Dictionary(Of String, SeatOffset) From {{"zzz", New SeatOffset With {.Up = 1}}}}.Validate()) AndAlso
              Refused(Sub() Call New LauncherSettings With {.SeatOffsets = New Dictionary(Of String, SeatOffset) From {{"frx", New SeatOffset With {.Up = 60}}}}.Validate()) AndAlso
              Refused(Sub() Call New LauncherSettings With {.SeatAllCars = New SeatOffset With {.Forward = -51}}.Validate()), "unknown cars and offsets beyond 50 cm are refused")
        check(SeatPositions.Effective(settings).Keys.SequenceEqual({"frx", "stx"}) AndAlso
              SeatPositions.Effective(New LauncherSettings With {.SeatSameForAll = True, .SeatAllCars = New SeatOffset With {.Up = 2}}).Count = EventCatalog.Current.Cars.Count AndAlso
              SeatPositions.Effective(New LauncherSettings With {.SeatSameForAll = True}).Count = 0, "a session moves the adjusted cars, or every car with the shared position")
        check(New SeatOffset With {.Sideways = -3, .Up = 2, .Forward = -5}.Describe() = "3 cm right · 2 cm up · 5 cm back", "offsets read as directions")
        check(ProfileValues.Capture(New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = ""}).ApplyTo(settings).SeatOffsets.Keys.SequenceEqual({"frx", "stx"}) AndAlso
              LauncherDefaults.All(settings).SeatOffsets.Count = 2, "settings profiles and Restore launcher defaults keep this PC's seat positions")

        ' A VR session journals each adjusted car's camera file with its offset (schema 6).
        Dim ctx = Context(root, "session")
        Dim seats = SeatPositions.Changes(ctx, settings)
        check(seats.Files.Keys.OrderBy(Function(key) key, StringComparer.Ordinal).SequenceEqual({"seat/frx", "seat/stx"}) AndAlso seats.Skipped.Count = 0, "the session changes the two adjusted cars' camera files")
        Dim changes = ExperimentalFiles.Changes(ctx, 1800, 1950).ToDictionary(Function(pair) pair.Key, Function(pair) pair.Value)
        For Each pair In seats.Files
            changes.Add(pair.Key, pair.Value)
        Next
        Dim transaction As New OwnedFileTransaction(ctx)
        check(Refused(Sub() transaction.Prepare(changes, experimental:=True, apply:=False)) AndAlso Not transaction.Pending, "seat changes without their offsets are refused before anything is staged")
        Dim wrong = seats.Offsets.ToDictionary(Function(pair) pair.Key, Function(pair) New SeatOffset With {.Up = 9})
        check(Refused(Sub() transaction.Prepare(changes, experimental:=True, apply:=False, seats:=wrong)) AndAlso Not transaction.Pending, "a camera file that isn't the recorded offset's edit is refused")
        check(Refused(Sub() transaction.Prepare(New Dictionary(Of String, Byte()) From {{"seat/frx", seats.Files("seat/frx")}}, apply:=False, seats:=seats.Offsets.Where(Function(pair) pair.Key = "frx").ToDictionary(Function(pair) pair.Key, Function(pair) pair.Value))) AndAlso Not transaction.Pending,
              "seat positions belong to a VR session")
        transaction.Prepare(changes, experimental:=True, apply:=False, seats:=seats.Offsets)
        Dim journal = SafeFiles.ReadJson(Of RecoveryJournal)(transaction.JournalPath)
        check(journal.Schema = 6 AndAlso journal.Seats.Keys.OrderBy(Function(key) key, StringComparer.Ordinal).SequenceEqual({"frx", "stx"}) AndAlso journal.Seats("stx").Up = 3, "the journal is schema 6 and records each car's offset")
        ' Deployed as the elevated worker would (ApplyPrepared needs the real game executable).
        For Each pair In changes
            Dim target = If(SeatPositions.IsTarget(pair.Key), SeatPositions.CameraPath(ctx, pair.Key.Substring(5)), If(pair.Key = "proxy", Path.Combine(ctx.GameRoot, "d3d11.dll"), If(pair.Key = "graphics", ctx.GraphicsPath, Path.Combine(ctx.GameRoot, "system", "hardware_settings_restrictions.xml"))))
            File.WriteAllBytes(target, pair.Value)
        Next
        Dim journalText = File.ReadAllText(transaction.JournalPath)
        File.WriteAllText(transaction.JournalPath, journalText.Replace("""Up"": 3", """Up"": 4"))
        check(journalText.Contains("""Up"": 3") AndAlso Refused(Sub() transaction.Recover()) AndAlso File.ReadAllBytes(SeatPositions.CameraPath(ctx, "stx")).SequenceEqual(seats.Files("seat/stx")), "a journal whose offset no longer matches the staged file is refused and nothing is touched")
        File.WriteAllText(transaction.JournalPath, journalText)
        check(transaction.Recover() AndAlso File.ReadAllBytes(SeatPositions.CameraPath(ctx, "frx")).SequenceEqual(original) AndAlso File.ReadAllBytes(SeatPositions.CameraPath(ctx, "stx")).SequenceEqual(original), "recovery restores every car's original camera file")

        ' A car whose camera file isn't usable keeps its own seat and is named.
        File.WriteAllBytes(SeatPositions.CameraPath(ctx, "stx"), Text.Encoding.UTF8.GetBytes("<not binary xml/>"))
        Dim partialSeats = SeatPositions.Changes(ctx, settings)
        check(partialSeats.Files.Keys.SequenceEqual({"seat/frx"}) AndAlso partialSeats.Skipped.SequenceEqual({EventCatalog.Current.FindCar("stx").Name}), "an unusable camera file is skipped and named, the others still move")
        check(SeatPositions.Changes(ctx, New LauncherSettings With {.SeatSameForAll = True, .SeatAllCars = New SeatOffset With {.Up = 1}}).Files.Count = 1, "with one position for every car, cars that aren't installed are simply left out")
    End Sub

    Public Sub RunUi(root As String, check As Action(Of Boolean, String))
        Dim ctx = Context(root, "ui")
        Call New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = ""}.Save(ctx)
        Using form As New MainForm(ctx)
            form.ShowInTaskbar = False : form.StartPosition = FormStartPosition.Manual : form.Location = New Drawing.Point(-30000, -30000)
            form.Show() : Application.DoEvents()
            Dim find = Function(name As String) form.Controls.Find(name, True).Single()
            ' Buttons only click while their tab shows.
            Dim tabs = DirectCast(find("LauncherTabs"), TabControl)
            tabs.SelectedTab = tabs.TabPages.Cast(Of TabPage)().Single(Function(page) page.Text = "Controls") : Application.DoEvents()
            Dim car = DirectCast(find("SeatCar"), ComboBox), sameForAll = DirectCast(find("SeatSameForAll"), CheckBox)
            Dim sideways = DirectCast(find("SeatSideways"), ValueSlider), height = DirectCast(find("SeatHeight"), ValueSlider), forward = DirectCast(find("SeatForward"), ValueSlider)
            Dim summary = find("SeatSummary")
            check(car.Items.Count = EventCatalog.Current.Cars.Count AndAlso summary.Text = "Every car uses DiRT 3's own seat." AndAlso form.SectionHeaders.Any(Function(header) header.Title = "Seat (VR)"), "Controls has a Seat (VR) section listing every car, all unchanged")
            car.SelectedIndex = car.Items.Cast(Of Object)().ToList().FindIndex(Function(item) item.ToString().StartsWith(EventCatalog.Current.FindCar("frx").Name, StringComparison.Ordinal))
            height.Value = 4 : forward.Value = -2 : Application.DoEvents()
            check(form.CurrentSettings().SeatOffsets.Keys.SequenceEqual({"frx"}) AndAlso form.CurrentSettings().SeatOffsets("frx").Up = 4 AndAlso form.CurrentSettings().SeatOffsets("frx").Forward = -2 AndAlso form.HasUnsavedSettings(),
                  "moving the sliders adjusts the chosen car")
            check(find("SeatHeightValue").Text = "4 cm up" AndAlso find("SeatForwardValue").Text = "2 cm back" AndAlso car.SelectedItem.ToString().EndsWith(" · adjusted", StringComparison.Ordinal) AndAlso summary.Text.StartsWith("1 car adjusted: ", StringComparison.Ordinal),
                  "the values read as directions and the car shows as adjusted (" & summary.Text & ")")
            car.SelectedIndex = (car.SelectedIndex + 1) Mod car.Items.Count : Application.DoEvents()
            check(height.Value = 0 AndAlso forward.Value = 0 AndAlso sideways.Value = 0, "another car shows its own (unchanged) position")
            sameForAll.Checked = True : Application.DoEvents()
            check(Not car.Enabled AndAlso find("SeatReset").Text = "Reset" AndAlso height.Value = 0, "Same position for all cars edits one shared position")
            sideways.Value = -3 : Application.DoEvents()
            check(form.CurrentSettings().SeatSameForAll AndAlso form.CurrentSettings().SeatAllCars.Sideways = -3 AndAlso summary.Text = "Every car: 3 cm right." AndAlso form.CurrentSettings().SeatOffsets.ContainsKey("frx"), "the shared position is kept apart from the per-car ones")
            DirectCast(find("SaveSettings"), Button).PerformClick()
            Dim saved = LauncherSettings.Load(ctx)
            check(saved.SeatSameForAll AndAlso saved.SeatAllCars.Sideways = -3 AndAlso saved.SeatOffsets("frx").Up = 4, "Save settings keeps the seat positions")
            sameForAll.Checked = False : Application.DoEvents()
            car.SelectedIndex = car.Items.Cast(Of Object)().ToList().FindIndex(Function(item) item.ToString().StartsWith(EventCatalog.Current.FindCar("frx").Name, StringComparison.Ordinal))
            Application.DoEvents()
            check(height.Value = 4, "turning the shared position off brings back each car's own")
            DirectCast(find("SeatReset"), Button).PerformClick() : Application.DoEvents()
            check(height.Value = 0 AndAlso form.CurrentSettings().SeatOffsets.Count = 0 AndAlso Not car.SelectedItem.ToString().Contains("adjusted"), "Reset this car goes back to DiRT 3's seat")
            form.Close()
        End Using
    End Sub
End Module
