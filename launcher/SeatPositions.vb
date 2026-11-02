Imports System.Text.Json.Serialization

' One car's seat offset for VR, in whole centimetres from the car's own eye point (Controls → Seat,
' #2fdaf181): sideways (+ left, as DiRT 3's x axis), up and forward.
Public Class SeatOffset
    Public Const Limit As Integer = 50
    Public Property Sideways As Integer
    Public Property Up As Integer
    Public Property Forward As Integer
    ' Session journal only: the car also starts in the cockpit view (LauncherSettings.StartInCockpit).
    <JsonIgnore(Condition:=JsonIgnoreCondition.WhenWritingDefault)>
    Public Property Cockpit As Boolean

    <JsonIgnore>
    Public ReadOnly Property IsZero As Boolean
        Get
            Return Sideways = 0 AndAlso Up = 0 AndAlso Forward = 0
        End Get
    End Property

    ' Leaves the camera file as it is (no offset, no cockpit start).
    <JsonIgnore>
    Public ReadOnly Property IsUnchanged As Boolean
        Get
            Return IsZero AndAlso Not Cockpit
        End Get
    End Property

    Public Sub Validate()
        If {Sideways, Up, Forward}.Any(Function(value) Math.Abs(value) > Limit) Then Throw New IOException("Seat offsets are at most " & Limit & " cm in each direction.")
    End Sub

    Public Function Copy() As SeatOffset
        Return New SeatOffset With {.Sideways = Sideways, .Up = Up, .Forward = Forward, .Cockpit = Cockpit}
    End Function

    Public Function SameAs(other As SeatOffset) As Boolean
        Return other IsNot Nothing AndAlso Sideways = other.Sideways AndAlso Up = other.Up AndAlso Forward = other.Forward
    End Function

    ' "3 cm left · 2 cm up · 5 cm back"; "unchanged" for zero.
    Public Function Describe() As String
        Dim parts As New List(Of String)
        If Sideways <> 0 Then parts.Add(Math.Abs(Sideways) & " cm " & If(Sideways > 0, "left", "right"))
        If Up <> 0 Then parts.Add(Math.Abs(Up) & " cm " & If(Up > 0, "up", "down"))
        If Forward <> 0 Then parts.Add(Math.Abs(Forward) & " cm " & If(Forward > 0, "forward", "back"))
        Return If(parts.Count = 0, "unchanged", String.Join(" · ", parts))
    End Function
End Class

' Seat positions for a VR session: which cars' camera files change and how. Each changed file is a
' journaled target "seat/<car>" (cars/models/<car>/cameras.xml, catalog cars only), recorded with its
' offset so recovery and the elevated worker accept only exactly that edit (SeatCameras).
Public NotInheritable Class SeatPositions
    Public Const TargetPrefix As String = "seat/"

    Private Sub New()
    End Sub

    Public Shared Function TargetFor(code As String) As String
        Return TargetPrefix & code
    End Function

    Public Shared Function IsTarget(target As String) As Boolean
        Return target IsNot Nothing AndAlso target.StartsWith(TargetPrefix, StringComparison.Ordinal)
    End Function

    ' The catalog car a target names; anything else is refused.
    Public Shared Function CodeOf(target As String) As String
        Dim code = If(IsTarget(target), target.Substring(TargetPrefix.Length), "")
        If EventCatalog.Current.FindCar(code) Is Nothing Then Throw New IOException("Unknown recovery target. The journal was preserved.")
        Return code
    End Function

    Public Shared Function CameraPath(context As InstallContext, code As String) As String
        If EventCatalog.Current.FindCar(code) Is Nothing Then Throw New IOException("Unknown car for seat adjustment.")
        Return Path.Combine(context.GameRoot, "cars", "models", code, "cameras.xml")
    End Function

    ' The seat offset, then (when asked) the cockpit start: start_in_cockpit(offset_seat(data, …)).
    Public Shared Function Apply(original As Byte(), offset As SeatOffset) As Byte()
        offset.Validate()
        Dim moved = SeatCameras.Offset(original, offset.Sideways / 100.0, offset.Up / 100.0, offset.Forward / 100.0)
        Return If(offset.Cockpit, SeatCameras.StartInCockpit(moved), moved)
    End Function

    ' The offset each car gets in a session: the shared one for every catalog car, or each car's own;
    ' cars left unchanged are not listed. The cockpit-only start is a VR option.
    Public Shared Function Effective(settings As LauncherSettings, Optional vr As Boolean = True) As SortedDictionary(Of String, SeatOffset)
        Dim result As New SortedDictionary(Of String, SeatOffset)(StringComparer.Ordinal)
        If settings.SeatSameForAll Then
            If settings.SeatAllCars IsNot Nothing AndAlso Not settings.SeatAllCars.IsZero Then
                For Each car In EventCatalog.Current.Cars
                    result(car.Code) = settings.SeatAllCars.Copy()
                Next
            End If
        ElseIf settings.SeatOffsets IsNot Nothing Then
            For Each pair In settings.SeatOffsets.Where(Function(item) item.Value IsNot Nothing AndAlso Not item.Value.IsZero)
                result(pair.Key) = pair.Value.Copy()
            Next
        End If
        For Each item In result.Values
            item.Cockpit = False
        Next
        If vr AndAlso settings.StartInCockpit Then
            For Each car In EventCatalog.Current.Cars
                If Not result.ContainsKey(car.Code) Then result(car.Code) = New SeatOffset
                result(car.Code).Cockpit = True
            Next
        End If
        Return result
    End Function

    ' The session's camera file changes. A car whose camera file is missing or not the expected
    ' format is left alone and named in Skipped, so one modded car doesn't block VR.
    Public Shared Function Changes(context As InstallContext, settings As LauncherSettings, Optional vr As Boolean = True) As (Files As Dictionary(Of String, Byte()), Offsets As Dictionary(Of String, SeatOffset), Skipped As List(Of String))
        Dim files As New Dictionary(Of String, Byte())(StringComparer.Ordinal)
        Dim offsets As New Dictionary(Of String, SeatOffset)(StringComparer.Ordinal)
        Dim skipped As New List(Of String)
        If Not vr AndAlso Not settings.SeatOnDesktop Then Return (files, offsets, skipped)
        For Each pair In Effective(settings, vr)
            Dim target = CameraPath(context, pair.Key)
            Try
                SafeFiles.NoLinks(target)
                If Not File.Exists(target) Then
                    ' Cars that aren't installed have nothing to adjust; only a car given its own position is named.
                    If settings.SeatSameForAll OrElse settings.SeatOffsets Is Nothing OrElse Not settings.SeatOffsets.ContainsKey(pair.Key) Then Continue For
                    Throw New IOException("missing")
                End If
                If New FileInfo(target).Length > 1024 * 1024 Then Throw New IOException("too large")
                files.Add(TargetFor(pair.Key), Apply(File.ReadAllBytes(target), pair.Value))
                offsets.Add(pair.Key, pair.Value)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                skipped.Add(EventCatalog.Current.FindCar(pair.Key).Name)
            End Try
        Next
        Return (files, offsets, skipped)
    End Function

    ' Whether Launch (desktop) moves seats: the option is on and some car has a position of its own.
    Public Shared Function OnDesktop(settings As LauncherSettings) As Boolean
        Return settings.SeatOnDesktop AndAlso Effective(settings, vr:=False).Count > 0
    End Function
End Class
