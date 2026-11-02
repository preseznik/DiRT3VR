Imports DiRT3VR
Imports System.IO
Imports System.IO.MemoryMappedFiles
Imports System.Text

' The in-game seat menu's shared memory (docs/seat-channel.md): the launcher's car table, keys and
' input; commits written as the renderer does, and saved back into the seat settings.
Module SeatChannelTests
    Private Function Refused(run As Action) As Boolean
        Try
            run()
        Catch ex As IOException
            Return True
        End Try
        Return False
    End Function

    ' A commit exactly as the renderer publishes one (DiRT2VR's Publish(commit:=True)).
    Private Sub Commit(renderer As MemoryMappedViewAccessor, index As Integer, x As Single, y As Single, z As Single)
        renderer.Write(92, renderer.ReadUInt32(92) + 1UI)
        renderer.Write(108, x) : renderer.Write(112, y) : renderer.Write(116, z)
        renderer.Write(120, index) : renderer.Write(124, renderer.ReadUInt32(124) + 1UI)
        renderer.Write(92, renderer.ReadUInt32(92) + 1UI)
    End Sub

    Private Function Text(renderer As MemoryMappedViewAccessor, offset As Integer, length As Integer) As String
        Dim bytes(length - 1) As Byte
        renderer.ReadArray(offset, bytes, 0, length)
        Return Encoding.UTF8.GetString(bytes, 0, Math.Max(0, Array.IndexOf(bytes, CByte(0))))
    End Function

    Public Sub Run(root As String, check As Action(Of Boolean, String))
        ' Centimetres (+left, up, forward) ↔ metres (x right, y up, z back), whole centimetres when saved.
        Dim position = SeatChannel.ToChannel(New SeatOffset With {.Sideways = 3, .Up = 4, .Forward = -2})
        check(Math.Abs(position.X + 0.03F) < 0.00001F AndAlso Math.Abs(position.Y - 0.04F) < 0.00001F AndAlso Math.Abs(position.Z - 0.02F) < 0.00001F, "seat offsets become DiRT2VR's right / up / back metres")
        Dim back = SeatChannel.FromChannel(-0.034F, 0.046F, 0.021F)
        check(back.Sideways = 3 AndAlso back.Up = 5 AndAlso back.Forward = -2, "a position comes back in whole centimetres (" & back.Describe() & ")")
        check(SeatChannel.FromChannel(Single.NaN, 0, 0) Is Nothing AndAlso SeatChannel.FromChannel(0, 0.6F, 0) Is Nothing AndAlso SeatChannel.FromChannel(0, 0, Single.PositiveInfinity) Is Nothing, "non-finite or out-of-range positions are ignored")

        Dim frx = EventCatalog.Current.FindCar("frx")
        Dim frxIndex = EventCatalog.Current.Cars.ToList().IndexOf(frx)
        Dim settings As New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = "", .SeatOffsets = New Dictionary(Of String, SeatOffset) From {{"frx", New SeatOffset With {.Up = 4}}}}
        Dim applied As New Dictionary(Of String, SeatOffset) From {{"frx", New SeatOffset With {.Up = 4}}}
        Dim name = "Local\DiRT3VR.SeatTest." & Guid.NewGuid().ToString("N")
        Using channel As New SeatChannel(settings, applied, name), opened = MemoryMappedFile.OpenExisting(name), renderer = opened.CreateViewAccessor()
            check(channel.Name = name AndAlso renderer.ReadUInt32(0) = &H54414553UI AndAlso renderer.ReadUInt32(4) = 1UI AndAlso renderer.ReadUInt32(8) = 16384UI AndAlso renderer.ReadUInt32(12) <> 0UI,
                  "the channel opens with DiRT3VR's header and a heartbeat")
            check(renderer.ReadInt32(88) = -1 AndAlso renderer.ReadInt32(120) = -1 AndAlso renderer.ReadUInt32(128) = 122UI AndAlso renderer.ReadUInt32(132) = 0UI, "no car yet, no commit, and F11 opens the menu")
            check(renderer.ReadUInt32(184) = CUInt(EventCatalog.Current.Cars.Count) AndAlso renderer.ReadUInt32(188) = 0UI, "every catalog car is listed, each with its own position")
            Dim entry = SeatChannel.Table + frxIndex * SeatChannel.Stride
            check(Text(renderer, entry, 16) = "frx" AndAlso Text(renderer, entry + 16, 48) = frx.Name.Substring(0, Math.Min(frx.Name.Length, 47)), "the car table has DiRT 3's code and the car's name")
            check(Math.Abs(renderer.ReadSingle(entry + 68) - 0.04F) < 0.00001F AndAlso Math.Abs(renderer.ReadSingle(entry + 80) - 0.04F) < 0.00001F,
                  "the car's position and the offset its camera file already holds")
            Dim other = SeatChannel.Table + ((frxIndex + 1) Mod EventCatalog.Current.Cars.Count) * SeatChannel.Stride
            check(renderer.ReadSingle(other + 64) = 0 AndAlso renderer.ReadSingle(other + 68) = 0 AndAlso renderer.ReadSingle(other + 80) = 0, "other cars start at DiRT 3's seat")
            Dim hint(799) As Byte
            renderer.ReadArray(SeatChannel.HintOffset, hint, 0, 800)
            check(Encoding.Unicode.GetString(hint).TrimEnd(ChrW(0)).StartsWith("Arrows / D-pad: move.", StringComparison.Ordinal), "the hint text under the panel")

            check(channel.ReadCommit() Is Nothing AndAlso Not channel.MenuOpen, "nothing to save before the menu is used")
            renderer.Write(80, 1UI)
            check(channel.MenuOpen, "the launcher sees the menu open")
            Commit(renderer, frxIndex, -0.02F, 0.07F, -0.03F)
            Dim saved = channel.ReadCommit()
            check(saved IsNot Nothing AndAlso Not saved.AllCars AndAlso saved.Car = "frx" AndAlso saved.Offset.Sideways = 2 AndAlso saved.Offset.Up = 7 AndAlso saved.Offset.Forward = 3, "a commit names the car and its new position")
            check(channel.ReadCommit() Is Nothing, "each commit is read once")
            renderer.Write(92, renderer.ReadUInt32(92) + 1UI) : renderer.Write(124, renderer.ReadUInt32(124) + 1UI)
            check(channel.ReadCommit() Is Nothing, "a commit still being written is left for the next poll")
            renderer.Write(92, renderer.ReadUInt32(92) + 1UI)
            Commit(renderer, 999, 0, 0.01F, 0)
            check(channel.ReadCommit() Is Nothing, "a commit for a car outside the table is ignored")
            Commit(renderer, frxIndex, 0, Single.NaN, 0)
            check(channel.ReadCommit() Is Nothing, "a commit with an invalid position is ignored")

            channel.WriteInput(1UI << 13, 1, 0, Guid.Empty, {&H1000UI, 0UI, 0UI, 0UI}, False)
            check(renderer.ReadUInt32(16) Mod 2UI = 0UI AndAlso renderer.ReadUInt32(16) > 0UI AndAlso renderer.ReadUInt32(20) = (1UI << 13) AndAlso renderer.ReadUInt32(36) = 1UI AndAlso renderer.ReadUInt32(60) = &H1000UI,
                  "controller input is published inside its sequence lock")

            SeatChannel.Apply(settings, saved)
            check(settings.SeatOffsets("frx").Up = 7 AndAlso settings.SeatOffsets("frx").Sideways = 2 AndAlso Not settings.SeatOffsets("frx").Cockpit, "the commit becomes the car's seat position")
            SeatChannel.Apply(settings, New SeatCommit With {.Car = "frx", .Offset = New SeatOffset})
            check(Not settings.SeatOffsets.ContainsKey("frx"), "back at DiRT 3's seat removes the car's own position")
            check(Refused(Sub() SeatChannel.Apply(settings, New SeatCommit With {.Car = "nope", .Offset = New SeatOffset With {.Up = 1}})), "an unknown car is refused")
        End Using

        ' One position for every car: commits are for all cars (index −2), never one car.
        Dim common As New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = "", .SeatSameForAll = True, .SeatAllCars = New SeatOffset With {.Forward = 5}}
        Dim sharedApplied = EventCatalog.Current.Cars.Take(2).ToDictionary(Function(car) car.Code, Function(car) New SeatOffset With {.Forward = 5})
        Dim sharedName = "Local\DiRT3VR.SeatTest." & Guid.NewGuid().ToString("N")
        Using channel As New SeatChannel(common, sharedApplied, sharedName), opened = MemoryMappedFile.OpenExisting(sharedName), renderer = opened.CreateViewAccessor()
            check(renderer.ReadUInt32(188) = 1UI AndAlso Math.Abs(renderer.ReadSingle(200) + 0.05F) < 0.00001F AndAlso Math.Abs(renderer.ReadSingle(212) + 0.05F) < 0.00001F AndAlso renderer.ReadSingle(SeatChannel.Table + 84) = 0,
                  "the shared position and what the camera files hold for it")
            Commit(renderer, frxIndex, 0, 0, -0.08F)
            check(channel.ReadCommit() Is Nothing, "a single car's commit doesn't apply with one position for every car")
            Commit(renderer, SeatChannel.AllCarsIndex, 0.01F, 0, -0.08F)
            Dim all = channel.ReadCommit()
            check(all IsNot Nothing AndAlso all.AllCars AndAlso all.Offset.Forward = 8 AndAlso all.Offset.Sideways = -1, "the commit is the position for every car")
            SeatChannel.Apply(common, all)
            check(common.SeatAllCars.Forward = 8 AndAlso common.SeatAllCars.Sideways = -1, "and becomes the shared seat position")
        End Using

        ' The menu key: F11 by default, never the recenter key, the arrows or Enter.
        Dim keys As New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = ""}
        keys.Validate()
        check(keys.SeatMenuKey = 122 AndAlso keys.SeatMenuModifiers = 0, "F11 opens the seat menu by default")
        For Each bad In {New LauncherSettings With {.SeatMenuKey = keys.RecenterKey}, New LauncherSettings With {.SeatMenuKey = 38}, New LauncherSettings With {.SeatMenuKey = 13},
                         New LauncherSettings With {.SeatMenuKey = 115, .SeatMenuModifiers = 2}, New LauncherSettings With {.SeatMenuKey = 122, .SeatMenuModifiers = 9}}
            Dim value = bad
            value.SteamRuntimeManifest = "" : value.VdxrRuntimeManifest = ""
            check(Refused(Sub() value.Validate()), "seat menu key " & bad.SeatMenuKey & "+" & bad.SeatMenuModifiers & " is refused")
        Next
        Dim none As New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = "", .SeatMenuKey = 0, .SeatMenuModifiers = 4}
        none.Validate()
        check(none.SeatMenuModifiers = 0, "no key means no modifiers")
        Dim profile = ProfileValues.Capture(New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = ""}).ApplyTo(New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = "", .SeatMenuKey = 120, .SeatMenuModifiers = 1})
        check(profile.SeatMenuKey = 120 AndAlso profile.SeatMenuModifiers = 1, "settings profiles keep this PC's seat menu key")
    End Sub
End Module
