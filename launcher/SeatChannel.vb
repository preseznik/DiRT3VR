Imports System.IO.MemoryMappedFiles
Imports System.Text
Imports System.Threading

' A seat position saved from the in-game seat menu: one car's, or the one for every car.
Public NotInheritable Class SeatCommit
    Public Property AllCars As Boolean
    Public Property Car As String = ""
    Public Property Offset As New SeatOffset
    ' Monitor sessions: the cockpit field of view saved with the commit (degrees from each car's own), and
    ' whether the commit is that alone (no car identified, commit index −3).
    Public Property Fov As Integer?
    Public Property FovOnly As Boolean
End Class

' The launcher's end of the in-game seat menu's shared memory (#bd820b32, launcher part #c137c681;
' contract in docs/seat-channel.md, ported from DiRT2VR's SeatChannel). The launcher writes the car
' table, the menu keys and the hint text; the renderer owns the menu and publishes its state and
' commits, which the launcher turns back into Controls → Seat positions.
Public NotInheritable Class SeatChannel
    Implements IDisposable

    Public Const Size As Integer = 16384
    Public Const Magic As UInteger = &H54414553UI
    Public Const ChannelVersion As Integer = 1
    Public Const MaxCars As Integer = 64
    Public Const Table As Integer = 256
    Public Const Stride As Integer = 96
    Public Const HintOffset As Integer = 10500
    Public Const EnvironmentName As String = "DIRT3VR_SEAT_CHANNEL"
    Public Const AllCarsIndex As Integer = -2
    Public Const FovOnlyIndex As Integer = -3
    ' Monitor sessions (docs/seat-channel.md): 216 the desktop flag, 220 the launch FOV offset the camera files hold,
    ' 224 the renderer's current and 228 its committed FOV offset.
    Public Const DesktopOffset As Integer = 216, LaunchFovOffset As Integer = 220, CurrentFovOffset As Integer = 224, CommittedFovOffset As Integer = 228

    Public ReadOnly Name As String
    Private ReadOnly mapping As MemoryMappedFile
    Private ReadOnly view As MemoryMappedViewAccessor
    Private ReadOnly cars As EventCatalog.EventCar()
    Private ReadOnly sameForAll As Boolean
    Private generation As UInteger
    Private sequence As UInteger
    Private lastHint As String

    ' settings: the positions the menu starts from; applied: what the session's camera files already hold
    ' (SeatPositions.Changes offsets), so the renderer applies only the difference live.
    ' desktop: a monitor session (Launch), whose camera files hold launchFov degrees on each car's own field of view.
    Public Sub New(settings As LauncherSettings, applied As IReadOnlyDictionary(Of String, SeatOffset), Optional channelName As String = Nothing, Optional desktop As Boolean = False, Optional launchFov As Integer = 0)
        Name = If(channelName, "Local\DiRT3VR.Seat." & Guid.NewGuid().ToString("N"))
        Me.Desktop = desktop
        cars = EventCatalog.Current.Cars.Take(MaxCars).ToArray()
        sameForAll = settings.SeatSameForAll
        mapping = MemoryMappedFile.CreateNew(Name, Size)
        view = mapping.CreateViewAccessor()
        view.Write(0, Magic) : view.Write(4, ChannelVersion) : view.Write(8, Size)
        Heartbeat()
        view.Write(88, -1) : view.Write(120, -1)
        view.Write(DesktopOffset, If(desktop, 1UI, 0UI)) : view.Write(LaunchFovOffset, If(desktop, launchFov, 0))
        view.Write(CurrentFovOffset, If(desktop, launchFov, 0)) : view.Write(CommittedFovOffset, If(desktop, launchFov, 0))
        ' Keys: [0] open / save & close, [1..6] left, right, up, down, back, forward (unbound: arrows in the menu).
        view.Write(128, CUInt(settings.SeatMenuKey)) : view.Write(132, CUInt(settings.SeatMenuModifiers))
        view.Write(184, CUInt(cars.Length))
        view.Write(188, If(sameForAll, 1UI, 0UI))
        WritePosition(192, If(settings.SeatAllCars, New SeatOffset))
        ' One position for every car: each installed car's camera file holds the same offset.
        WritePosition(204, If(sameForAll AndAlso applied IsNot Nothing AndAlso applied.Count > 0, applied.Values.First(), New SeatOffset))
        For i = 0 To cars.Length - 1
            WriteText(Table + i * Stride, 16, cars(i).Code)
            WriteText(Table + i * Stride + 16, 48, cars(i).Name)
            Dim own As SeatOffset = Nothing, written As SeatOffset = Nothing
            WritePosition(Table + i * Stride + 64, If(settings.SeatOffsets IsNot Nothing AndAlso settings.SeatOffsets.TryGetValue(cars(i).Code, own), own, New SeatOffset))
            WritePosition(Table + i * Stride + 76, If(Not sameForAll AndAlso applied IsNot Nothing AndAlso applied.TryGetValue(cars(i).Code, written), written, New SeatOffset))
        Next
        WriteHint(SeatMenuInput.Hint(Nothing, Nothing, desktop))
    End Sub

    ' A monitor session's channel (Launch) rather than a VR one.
    Public ReadOnly Property Desktop As Boolean

    ' Whether Launch (on the monitor) gets the seat menu: the option is on and a key or button opens it.
    Public Shared Function OnDesktop(settings As LauncherSettings) As Boolean
        Return settings.SeatMenuOnDesktop AndAlso (settings.SeatMenuKey <> 0 OrElse settings.SeatMenuButton IsNot Nothing)
    End Function

    ' Launcher centimetres (+left, up, forward) → channel metres (x right, y up, z back).
    Public Shared Function ToChannel(offset As SeatOffset) As (X As Single, Y As Single, Z As Single)
        Return (-offset.Sideways / 100.0F, offset.Up / 100.0F, -offset.Forward / 100.0F)
    End Function

    ' Channel metres → whole centimetres; Nothing for a value that isn't a finite position within 50 cm.
    Public Shared Function FromChannel(x As Single, y As Single, z As Single) As SeatOffset
        If {x, y, z}.Any(Function(value) Not Single.IsFinite(value) OrElse Math.Abs(value) > 0.5F + 0.0001F) Then Return Nothing
        Dim centimetres = Function(value As Single) CInt(Math.Round(value * 100.0, MidpointRounding.AwayFromZero))
        Dim result As New SeatOffset With {.Sideways = -centimetres(x), .Up = centimetres(y), .Forward = -centimetres(z)}
        result.Sideways = Math.Clamp(result.Sideways, -SeatOffset.Limit, SeatOffset.Limit)
        result.Up = Math.Clamp(result.Up, -SeatOffset.Limit, SeatOffset.Limit)
        result.Forward = Math.Clamp(result.Forward, -SeatOffset.Limit, SeatOffset.Limit)
        Return result
    End Function

    Private Sub WritePosition(offset As Integer, value As SeatOffset)
        Dim position = ToChannel(value)
        view.Write(offset, position.X) : view.Write(offset + 4, position.Y) : view.Write(offset + 8, position.Z)
    End Sub

    Private Sub WriteText(offset As Integer, length As Integer, text As String)
        Dim bytes(length - 1) As Byte
        Dim encoded = Encoding.UTF8.GetBytes(text)
        ' Keep a NUL at the end and never cut a UTF-8 sequence in half.
        Dim count = Math.Min(encoded.Length, length - 1)
        Do While count > 0 AndAlso count < encoded.Length AndAlso (encoded(count) And &HC0) = &H80
            count -= 1
        Loop
        Array.Copy(encoded, bytes, count)
        view.WriteArray(offset, bytes, 0, bytes.Length)
    End Sub

    Public Sub WriteHint(text As String)
        If text = lastHint Then Return
        lastHint = text
        Dim bytes(799) As Byte
        Encoding.Unicode.GetBytes(text.Substring(0, Math.Min(399, text.Length))).CopyTo(bytes, 0)
        view.WriteArray(HintOffset, bytes, 0, bytes.Length)
    End Sub

    Public Sub Heartbeat()
        view.Write(12, CUInt(Environment.TickCount64 And &HFFFFFFFFL))
    End Sub

    ' The held controller buttons for the menu (docs/seat-channel.md bits) and the buttons to keep from
    ' the game, inside the input seqlock. source: 0 none, 1 XInput, 2 DirectInput.
    Public Sub WriteInput(held As UInteger, source As Integer, slot As Integer, device As Guid, buttons As UInteger(), hat As Boolean)
        If buttons Is Nothing OrElse buttons.Length <> 4 Then Throw New ArgumentException("Four button masks are required.", NameOf(buttons))
        sequence = view.ReadUInt32(16) Or 1UI
        view.Write(16, sequence) : Thread.MemoryBarrier()
        view.Write(20, held) : view.Write(36, CUInt(source)) : view.Write(40, CUInt(slot))
        view.WriteArray(44, device.ToByteArray(), 0, 16)
        view.WriteArray(60, buttons, 0, 4) : view.Write(76, If(hat, 1UI, 0UI))
        Thread.MemoryBarrier() : sequence += 1UI : view.Write(16, sequence)
        Heartbeat()
    End Sub

    ' Whether the renderer shows the menu now.
    Public ReadOnly Property MenuOpen As Boolean
        Get
            Return view.ReadUInt32(80) <> 0UI
        End Get
    End Property

    ' A new commit from the renderer, or Nothing (none, mid-write, or not a valid position).
    Public Function ReadCommit() As SeatCommit
        Dim before = view.ReadUInt32(92)
        If (before And 1UI) <> 0UI Then Return Nothing
        Dim nextGeneration = view.ReadUInt32(124), index = view.ReadInt32(120)
        Dim x = view.ReadSingle(108), y = view.ReadSingle(112), z = view.ReadSingle(116)
        Dim fov = view.ReadInt32(CommittedFovOffset)
        Thread.MemoryBarrier()
        If before <> view.ReadUInt32(92) OrElse nextGeneration = generation Then Return Nothing
        generation = nextGeneration
        ' On the monitor every commit carries the field of view; with no car identified it is all there is.
        Dim committedFov As Integer? = Nothing
        If Desktop Then
            If fov < -SeatOffset.FovNarrower OrElse fov > SeatOffset.FovWider Then Return Nothing
            committedFov = fov
            If index = FovOnlyIndex Then Return New SeatCommit With {.FovOnly = True, .Fov = fov}
        End If
        Dim offset = FromChannel(x, y, z)
        If offset Is Nothing Then Return Nothing
        If sameForAll Then
            If index <> AllCarsIndex Then Return Nothing
            Return New SeatCommit With {.AllCars = True, .Offset = offset, .Fov = committedFov}
        End If
        If index < 0 OrElse index >= cars.Length Then Return Nothing
        Return New SeatCommit With {.Car = cars(index).Code, .Offset = offset, .Fov = committedFov}
    End Function

    ' The settings with a commit applied: the car's own position (removed when back to DiRT 3's seat) or
    ' the one for every car. The cockpit-only start is a separate setting and never part of an offset.
    Public Shared Sub Apply(settings As LauncherSettings, commit As SeatCommit)
        If commit.Fov.HasValue Then settings.DesktopFov = commit.Fov.Value
        If commit.FovOnly Then
            settings.Validate()
            Return
        End If
        commit.Offset.Validate()
        Dim offset = commit.Offset.Copy()
        offset.ClearSessionFlags()
        If commit.AllCars Then
            settings.SeatAllCars = offset
        Else
            If EventCatalog.Current.FindCar(commit.Car) Is Nothing Then Throw New IOException("The seat menu named an unknown car.")
            If settings.SeatOffsets Is Nothing Then settings.SeatOffsets = New Dictionary(Of String, SeatOffset)
            If offset.IsZero Then settings.SeatOffsets.Remove(commit.Car) Else settings.SeatOffsets(commit.Car) = offset
        End If
        settings.Validate()
    End Sub

    ' Reads a new commit and saves it into the launcher settings on disk; the commit, or Nothing.
    Public Shared Function SaveCommit(context As InstallContext, channel As SeatChannel) As SeatCommit
        If channel Is Nothing Then Return Nothing
        channel.Heartbeat()
        Dim commit = channel.ReadCommit()
        If commit Is Nothing Then Return Nothing
        Dim saved = LauncherSettings.Load(context)
        Apply(saved, commit)
        saved.Save(context)
        Return commit
    End Function

    Public Sub Dispose() Implements IDisposable.Dispose
        view.Dispose() : mapping.Dispose()
    End Sub
End Class
