Imports System.Buffers.Binary
Imports System.Globalization
Imports System.Text

' Seat position edits for DiRT 3's car camera files (cars/models/<car>/cameras.xml), ported from
' Wrench's tools/seat_cameras.py (#2fdaf181, docs/research/seat-adjustment.md). The files are Ego binary
' XML: a string container (strings, NUL-padded to 16 bytes, one offset per string), a 24-byte element
' table and an 8-byte attribute table. An edit changes attribute values only: a new value is appended to
' the string table (or an equal string reused) and only the cockpit view's position x/y/z point at it, so
' every other byte stays as the game shipped it.
Public NotInheritable Class SeatCameras
    ' A seat offset is a small adjustment from the car's own eye point, in metres per axis.
    Public Const MaxOffset As Double = 0.5
    Private Const MaxBytes As Integer = 1024 * 1024
    Private Shared ReadOnly Magic As Byte() = {&H1A, &H22, &H52, &H72}
    Private Shared ReadOnly StringsContainer As Byte() = {&H17, &H22, &H52, &H72}
    Private Shared ReadOnly StringsChunk As Byte() = {&H1D, &H22, &H52, &H72}
    Private Shared ReadOnly OffsetsChunk As Byte() = {&H1E, &H22, &H52, &H72}
    Private Shared ReadOnly ElementsChunk As Byte() = {&H1B, &H22, &H52, &H72}
    Private Shared ReadOnly AttributesChunk As Byte() = {&H1C, &H22, &H52, &H72}
    Private Shared ReadOnly Utf8 As New UTF8Encoding(False, True)

    Private Sub New()
    End Sub

    Friend NotInheritable Class BinXml
        Public ReadOnly Strings As New List(Of String)
        ' name, value, attribute count, first attribute, child count, first child
        Public ReadOnly Elements As New List(Of Integer())
        ' name, value (string indices)
        Public ReadOnly Attributes As New List(Of Integer())

        Public Function Text(index As Integer) As String
            If index < 0 OrElse index >= Strings.Count Then Throw New IOException("Invalid camera file string index.")
            Return Strings(index)
        End Function

        Public Function Name(element As Integer) As String
            Return Text(Elements(element)(0))
        End Function

        Public Function AttributeSlots(element As Integer) As Dictionary(Of String, Integer)
            Dim item = Elements(element)
            Dim slots As New Dictionary(Of String, Integer)(StringComparer.Ordinal)
            For slot = item(3) To item(3) + item(2) - 1
                Dim key = Text(Attributes(slot)(0))
                If slots.ContainsKey(key) Then Throw New IOException("Duplicate camera file attribute.")
                slots.Add(key, slot)
            Next
            Return slots
        End Function

        Public Function AttributesOf(element As Integer) As Dictionary(Of String, String)
            Return AttributeSlots(element).ToDictionary(Function(pair) pair.Key, Function(pair) Text(Attributes(pair.Value)(1)), StringComparer.Ordinal)
        End Function

        Public Function Children(element As Integer) As IEnumerable(Of Integer)
            Dim item = Elements(element)
            Return If(item(4) = 0, Enumerable.Empty(Of Integer)(), Enumerable.Range(item(5), item(4)))
        End Function

        Public Sub SetAttribute(element As Integer, key As String, value As String)
            Dim slot As Integer
            If Not AttributeSlots(element).TryGetValue(key, slot) Then Throw New IOException("The camera element has no " & key & " attribute.")
            If Text(Attributes(slot)(1)) = value Then Return
            If value.Contains(ChrW(0)) Then Throw New IOException("Camera values cannot contain NUL.")
            ' Strings are shared between attributes, so a new value never edits an existing string.
            Dim index = Strings.IndexOf(value)
            If index < 0 Then
                index = Strings.Count
                Strings.Add(value)
            End If
            Attributes(slot)(1) = index
        End Sub
    End Class

    Private Shared Function UInt(data As Byte(), position As Integer) As Long
        Return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position, 4))
    End Function

    Private Shared Function Matches(data As Byte(), position As Integer, signature As Byte()) As Boolean
        Return position >= 0 AndAlso position + 4 <= data.Length AndAlso data.AsSpan(position, 4).SequenceEqual(signature)
    End Function

    ' A chunk's payload range and the offset after it.
    Private Shared Function Chunk(data As Byte(), cursor As Integer, signature As Byte()) As (Start As Integer, Length As Integer, NextOffset As Integer)
        If cursor + 8 > data.Length OrElse Not Matches(data, cursor, signature) Then Throw New IOException("Unexpected camera file chunk.")
        Dim size = UInt(data, cursor + 4)
        Dim finish = cursor + 8L + size
        If finish > data.Length Then Throw New IOException("Truncated camera file chunk.")
        Return (cursor + 8, CInt(size), CInt(finish))
    End Function

    Friend Shared Function Decode(data As Byte()) As BinXml
        If data Is Nothing OrElse data.Length < 24 OrElse data.Length > MaxBytes OrElse Not Matches(data, 0, Magic) OrElse UInt(data, 4) <> data.Length - 8 Then Throw New IOException("Not a supported camera file.")
        If Not Matches(data, 8, StringsContainer) Then Throw New IOException("The camera file has no string container.")
        Dim raw = Chunk(data, 16, StringsChunk)
        Dim offsetsRange = Chunk(data, raw.NextOffset, OffsetsChunk)
        If UInt(data, 12) <> offsetsRange.NextOffset - 16 OrElse offsetsRange.Length Mod 4 <> 0 Then Throw New IOException("Invalid camera file string container.")
        Dim document As New BinXml()
        Dim position = 0
        For index = 0 To offsetsRange.Length \ 4 - 1
            Dim stringStart = UInt(data, offsetsRange.Start + index * 4)
            If stringStart <> position Then Throw New IOException("Camera file string offsets are not sequential.")
            Dim finish = Array.IndexOf(data, CByte(0), raw.Start + position, raw.Length - position)
            If finish < 0 Then Throw New IOException("Camera file string offsets are not sequential.")
            Try
                document.Strings.Add(Utf8.GetString(data, raw.Start + position, finish - raw.Start - position))
            Catch ex As DecoderFallbackException
                Throw New IOException("Invalid camera file text.", ex)
            End Try
            position = finish - raw.Start + 1
        Next
        For index = position To raw.Length - 1
            If data(raw.Start + index) <> 0 Then Throw New IOException("Unexpected camera file string padding.")
        Next
        If raw.Length Mod 16 <> 0 Then Throw New IOException("Unexpected camera file string padding.")
        Dim elements = Chunk(data, offsetsRange.NextOffset, ElementsChunk)
        Dim attributes = Chunk(data, elements.NextOffset, AttributesChunk)
        If attributes.NextOffset <> data.Length OrElse elements.Length Mod 24 <> 0 OrElse attributes.Length Mod 8 <> 0 Then Throw New IOException("Invalid camera file element or attribute table.")
        For row = elements.Start To elements.Start + elements.Length - 1 Step 24
            Dim at = row
            document.Elements.Add(Enumerable.Range(0, 6).Select(Function(field) BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at + field * 4, 4))).ToArray())
        Next
        For row = attributes.Start To attributes.Start + attributes.Length - 1 Step 8
            document.Attributes.Add({BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(row, 4)), BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(row + 4, 4))})
        Next
        For Each item In document.Elements
            document.Text(item(0))
            If item(2) < 0 OrElse item(4) < 0 OrElse
               (item(2) <> 0 AndAlso (item(3) < 0 OrElse item(3) > document.Attributes.Count - item(2))) OrElse
               (item(4) <> 0 AndAlso (item(5) < 0 OrElse item(5) > document.Elements.Count - item(4))) Then Throw New IOException("Invalid camera file table range.")
        Next
        For Each pair In document.Attributes
            document.Text(pair(0)) : document.Text(pair(1))
        Next
        Return document
    End Function

    Friend Shared Function Encode(document As BinXml) As Byte()
        Dim raw As New List(Of Byte)
        Dim offsets As New List(Of Integer)
        For Each value In document.Strings
            offsets.Add(raw.Count)
            raw.AddRange(Utf8.GetBytes(value)) : raw.Add(0)
        Next
        ' Every stock file pads the strings with 0-15 NULs to a multiple of 16 bytes.
        While raw.Count Mod 16 <> 0
            raw.Add(0)
        End While
        Using body As New MemoryStream()
            Dim stringsLength = 8 + raw.Count, offsetsLength = 8 + 4 * offsets.Count
            Write(body, StringsContainer, stringsLength + offsetsLength)
            Write(body, StringsChunk, raw.Count) : body.Write(raw.ToArray())
            Write(body, OffsetsChunk, 4 * offsets.Count)
            For Each value In offsets
                WriteInt(body, value)
            Next
            Write(body, ElementsChunk, 24 * document.Elements.Count)
            For Each item In document.Elements
                For Each value In item
                    WriteInt(body, value)
                Next
            Next
            Write(body, AttributesChunk, 8 * document.Attributes.Count)
            For Each pair In document.Attributes
                WriteInt(body, pair(0)) : WriteInt(body, pair(1))
            Next
            Using result As New MemoryStream()
                Write(result, Magic, CInt(body.Length))
                body.Position = 0 : body.CopyTo(result)
                Return result.ToArray()
            End Using
        End Using
    End Function

    Private Shared Sub Write(stream As Stream, signature As Byte(), length As Integer)
        stream.Write(signature) : WriteInt(stream, length)
    End Sub

    Private Shared Sub WriteInt(stream As Stream, value As Integer)
        Dim bytes(3) As Byte
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value)
        stream.Write(bytes)
    End Sub

    ' The View elements of the single ViewManager, in file order.
    Private Shared Function Views(document As BinXml) As List(Of Integer)
        If document.Elements.Count = 0 OrElse document.Name(0) <> "VehicleViewDefinition" OrElse document.Children(0).Count() <> 1 Then Throw New IOException("Unexpected camera definition root.")
        Dim manager = document.Children(0).Single()
        If document.Name(manager) <> "ViewManager" Then Throw New IOException("The camera file has no ViewManager.")
        Return document.Children(manager).Where(Function(child) document.Name(child) = "View").ToList()
    End Function

    Private Shared Function HeadCam(document As BinXml) As Integer
        Dim found = Views(document).Where(Function(view) GetValue(document.AttributesOf(view), "ident") = "head-cam").ToList()
        If found.Count <> 1 OrElse GetValue(document.AttributesOf(found(0)), "type") <> "Head" Then Throw New IOException("The camera file needs exactly one Head view named head-cam.")
        Return found(0)
    End Function

    Private Shared Function GetValue(values As Dictionary(Of String, String), key As String) As String
        Dim value As String = Nothing
        Return If(values.TryGetValue(key, value), value, Nothing)
    End Function

    Private Shared Iterator Function Descendants(document As BinXml, element As Integer, visited As HashSet(Of Integer)) As IEnumerable(Of Integer)
        For Each child In document.Children(element)
            ' Malformed tables could loop; the game's files are trees.
            If Not visited.Add(child) Then Throw New IOException("The camera file's elements are not a tree.")
            Yield child
            For Each nested In Descendants(document, child, visited)
                Yield nested
            Next
        Next
    End Function

    Private Shared Function HeadPosition(document As BinXml) As (Element As Integer, X As Double, Y As Double, Z As Double)
        Dim view = HeadCam(document)
        Dim found = Descendants(document, view, New HashSet(Of Integer) From {view}).
            Where(Function(item) document.Name(item) = "Parameter" AndAlso GetValue(document.AttributesOf(item), "name") = "position").ToList()
        If found.Count <> 1 Then Throw New IOException("The camera file needs one head-cam position.")
        Dim values = document.AttributesOf(found(0))
        If GetValue(values, "type") <> "vector3" OrElse Not values.Keys.OrderBy(Function(key) key, StringComparer.Ordinal).SequenceEqual({"name", "type", "x", "y", "z"}) Then Throw New IOException("Unexpected head-cam position.")
        Dim axes = {"x", "y", "z"}.Select(Function(axis) Number(values(axis))).ToArray()
        Return (found(0), axes(0), axes(1), axes(2))
    End Function

    Private Shared Function Number(text As String) As Double
        Dim value As Double
        If Not Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, value) OrElse Not Double.IsFinite(value) Then Throw New IOException("Non-numeric head-cam position.")
        Return value
    End Function

    ' A steady cockpit camera for VR (#cd56379b; Wrench's head-cam inventory of the stock cars): DiRT 3's own
    ' G-force head and body movement, acceleration shake, buffeting and impact distortion, which move the view
    ' on top of head tracking. Values in the game's own format.
    Public Shared ReadOnly SteadyParameters As (Name As String, Type As String, Value As String)() = {
        ("maxHeadOffset", "scalar", "0.0000"), ("maxBodyOffset", "scalar", "0.0000"), ("headBuffeting", "bool", "false"),
        ("shakeAngleScalingX", "scalar", "0.0000"), ("minShakeAngleX", "scalar", "0.0000"), ("maxShakeAngleX", "scalar", "0.0000"),
        ("shakeAngleScalingY", "scalar", "0.0000"), ("minShakeAngleY", "scalar", "0.0000"), ("maxShakeAngleY", "scalar", "0.0000"),
        ("cdEnabled", "bool", "false")}

    ' The Parameter elements under a view (in its groups too), by name; a name that appears twice is refused.
    Private Shared Function ViewParameters(document As BinXml, view As Integer) As Dictionary(Of String, Integer)
        Dim result As New Dictionary(Of String, Integer)(StringComparer.Ordinal)
        For Each item In Descendants(document, view, New HashSet(Of Integer) From {view})
            If document.Name(item) <> "Parameter" Then Continue For
            Dim name = GetValue(document.AttributesOf(item), "name")
            If name Is Nothing Then Continue For
            If result.ContainsKey(name) Then Throw New IOException("The cockpit view has two " & name & " parameters.")
            result.Add(name, item)
        Next
        Return result
    End Function

    ' Cockpit comfort in the head-cam view: hide the driver and/or the co-driver, and a steady camera.
    ' A parameter the car doesn't have stays absent; one of another type or shape is refused.
    Public Shared Function Comfort(data As Byte(), hideDriver As Boolean, hideCoDriver As Boolean, steady As Boolean) As Byte()
        Dim document = Decode(data)
        Dim parameters = ViewParameters(document, HeadCam(document))
        Dim change = Sub(name As String, type As String, value As String)
                         Dim element As Integer
                         If Not parameters.TryGetValue(name, element) Then Return
                         Dim values = document.AttributesOf(element)
                         If GetValue(values, "type") <> type OrElse Not values.Keys.OrderBy(Function(key) key, StringComparer.Ordinal).SequenceEqual({"name", "type", "value"}) Then
                             Throw New IOException("Unexpected cockpit view parameter " & name & ".")
                         End If
                         document.SetAttribute(element, "value", value)
                     End Sub
        If hideDriver Then change("DisableDriver", "bool", "true")
        If hideCoDriver Then change("DisableCoDriver", "bool", "true")
        If steady Then
            For Each parameter In SteadyParameters
                change(parameter.Name, parameter.Type, parameter.Value)
            Next
        End If
        Return Encode(document)
    End Function

    ' The cockpit view's field of view on the monitor (#b330cc4f): the car's own plus change degrees, in the
    ' game's format. A car without one, or with a value that isn't a plausible angle, is refused.
    Public Shared Function FieldOfView(data As Byte(), change As Integer) As Byte()
        Dim document = Decode(data)
        Dim element As Integer
        If Not ViewParameters(document, HeadCam(document)).TryGetValue("fov", element) Then Throw New IOException("The cockpit view has no field of view.")
        Dim values = document.AttributesOf(element)
        Dim own As Double
        If GetValue(values, "type") <> "scalar" OrElse Not values.Keys.OrderBy(Function(key) key, StringComparer.Ordinal).SequenceEqual({"name", "type", "value"}) OrElse
           Not Double.TryParse(values("value"), NumberStyles.Float, CultureInfo.InvariantCulture, own) OrElse Not Double.IsFinite(own) OrElse own < 20 OrElse own > 120 Then
            Throw New IOException("Unexpected cockpit field of view.")
        End If
        If change < -SeatOffset.FovNarrower OrElse change > SeatOffset.FovWider OrElse own + change < 10 OrElse own + change > 150 Then Throw New IOException("The field of view change is out of range.")
        document.SetAttribute(element, "value", (own + change).ToString("F4", CultureInfo.InvariantCulture))
        Return Encode(document)
    End Function

    ' A view's parameter values by name (ident "head-cam" for the cockpit), for checks and the tests.
    Public Shared Function ViewValues(data As Byte(), ident As String) As Dictionary(Of String, String)
        Dim document = Decode(data)
        Dim found = Views(document).Where(Function(view) GetValue(document.AttributesOf(view), "ident") = ident).ToList()
        If found.Count <> 1 Then Throw New IOException("The camera file has no single " & ident & " view.")
        Return ViewParameters(document, found(0)).ToDictionary(Function(pair) pair.Key, Function(pair) GetValue(document.AttributesOf(pair.Value), "value"), StringComparer.Ordinal)
    End Function

    ' Wrench's cockpit_first (iztok: start in the cockpit, hide nothing): head-cam's record moves to the front of
    ' the view list. The Views are sibling records, each pointing at its own attributes and children, so rotating
    ' them reorders the list and leaves every subtree, string and attribute where it was; twice changes nothing.
    ' DiRT3VR's renderer (DIRT3VR_START_IN_COCKPIT) keeps the race in that first view instead of DiRT 3's chase_close.
    Public Shared Function CockpitFirst(data As Byte()) As Byte()
        Dim document = Decode(data)
        Dim order = Views(document)
        Dim cockpit = HeadCam(document)
        Dim first = order(0)
        If Not order.Take(order.IndexOf(cockpit) + 1).SequenceEqual(Enumerable.Range(first, cockpit - first + 1)) Then Throw New IOException("The camera file's views before the cockpit view are not in one block.")
        Dim record = document.Elements(cockpit)
        document.Elements.RemoveAt(cockpit)
        document.Elements.Insert(first, record)
        Return Encode(document)
    End Function

    ' Wrench's start_in_cockpit, the earlier cockpit start, kept to recover its sessions: DiRT 3 starts a race with
    ' the first visible view, so hiding the visible views listed before head-cam starts it in the cockpit.
    Public Shared Function StartInCockpit(data As Byte()) As Byte()
        Dim document = Decode(data)
        Dim cockpit = HeadCam(document)
        For Each view In Views(document)
            If view = cockpit Then Exit For
            If GetValue(document.AttributesOf(view), "hidden") = "false" Then document.SetAttribute(view, "hidden", "true")
        Next
        Return Encode(document)
    End Function

    ' The cockpit eye point of a camera file, in metres (x left, y up, z forward).
    Public Shared Function Eye(data As Byte()) As (X As Double, Y As Double, Z As Double)
        Dim position = HeadPosition(Decode(data))
        Return (position.X, position.Y, position.Z)
    End Function

    ' Moves the cockpit eye point by (sideways, up, forward) metres along the car's x, y, z; values are
    ' written with four decimals, as the game's own.
    Public Shared Function Offset(data As Byte(), sideways As Double, up As Double, forward As Double) As Byte()
        For Each value In {sideways, up, forward}
            If Not Double.IsFinite(value) OrElse Math.Abs(value) > MaxOffset Then Throw New IOException("Seat offsets must be within ±0.5 m.")
        Next
        Dim document = Decode(data)
        Dim position = HeadPosition(document)
        document.SetAttribute(position.Element, "x", (position.X + sideways).ToString("F4", CultureInfo.InvariantCulture))
        document.SetAttribute(position.Element, "y", (position.Y + up).ToString("F4", CultureInfo.InvariantCulture))
        document.SetAttribute(position.Element, "z", (position.Z + forward).ToString("F4", CultureInfo.InvariantCulture))
        Return Encode(document)
    End Function
End Class
