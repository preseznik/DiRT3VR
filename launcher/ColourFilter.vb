Imports System.Buffers.Binary

' The colour filter off (#24f76866; the launcher's port of Wrench's tools/colour_filter.py, docs/research/colour-filter.md).
' DiRT 3's green/cyan tint comes from the colour palettes the track effects name (cross_nathan, kenya, shibuya, ...):
' textures in postprocess\palettes.pssg (Codemasters PSSG, big-endian), each a 32x256 ui8x4 3D colour lookup table in
' one 32,768-byte image block. Off writes the identity table into every palette block, for that session only
' (PostEffects.PaletteTarget, restored like the effect files): same size, only the texels change, and doing it again
' changes nothing. Tone mapping, colour balance, bloom and the vignette stay as they are. Anything that isn't palettes
' of exactly that shape is refused, so the colour filter then stays on.
Public NotInheritable Class ColourFilter
    Public Const Width As Integer = 32
    Public Const Height As Integer = 256
    Public Const Format As String = "ui8x4"
    Public Const BlockSize As Integer = Width * Height * 4
    ' The reference tool's limit; DiRT 3's palettes file is far smaller.
    Public Const MaximumSize As Long = 8 * 1024 * 1024
    ' The table's levels: red down the 16 tiles, green across a tile, blue down a tile.
    Private Const Reds As Integer = 16, Greens As Integer = 32, Blues As Integer = 16
    Private Const MaximumDepth As Integer = 32

    ' The neutral table in the palettes' layout: texel (x = g, y = r * 16 + b) = (r, g, b) at full scale, alpha 255.
    Public Shared ReadOnly Identity As Byte() = MakeIdentity()

    ' One texture of the file: its id, size, format and image blocks (offset and length of each block's data).
    Public NotInheritable Class Palette
        Public Property Id As String
        Public Property Width As Object
        Public Property Height As Object
        Public Property Format As Object
        Public ReadOnly Property Blocks As New List(Of (Offset As Integer, Length As Integer))
    End Class

    Private Sub New()
    End Sub

    Public Shared Function PalettePath(context As InstallContext) As String
        Return Path.Combine(context.GameRoot, "postprocess", "palettes.pssg")
    End Function

    Private Shared Function MakeIdentity() As Byte()
        Dim table(BlockSize - 1) As Byte
        For r = 0 To Reds - 1
            For b = 0 To Blues - 1
                For g = 0 To Greens - 1
                    Dim offset = ((r * Blues + b) * Width + g) * 4
                    table(offset) = CByte(Math.Round(r * 255.0 / (Reds - 1)))
                    table(offset + 1) = CByte(Math.Round(g * 255.0 / (Greens - 1)))
                    table(offset + 2) = CByte(Math.Round(b * 255.0 / (Blues - 1)))
                    table(offset + 3) = 255
                Next
            Next
        Next
        Return table
    End Function

    ' The palettes file with every palette neutral; the same size, and applying it twice gives the same bytes.
    Public Shared Function Neutral(data As Byte()) As Byte()
        Dim result = DirectCast(data.Clone(), Byte())
        For Each texture In Checked(data)
            Buffer.BlockCopy(Identity, 0, result, texture.Blocks(0).Offset, BlockSize)
        Next
        Return result
    End Function

    ' Every palette, each 32x256 ui8x4 with one image block of 32,768 bytes; anything else is refused.
    Public Shared Function Checked(data As Byte()) As IReadOnlyList(Of Palette)
        Dim found = Palettes(data)
        If found.Count = 0 Then Throw New IOException("No colour palettes in this file.")
        For Each texture In found
            If Not (TypeOf texture.Width Is UInteger AndAlso CUInt(texture.Width) = Width AndAlso TypeOf texture.Height Is UInteger AndAlso CUInt(texture.Height) = Height AndAlso
                    TypeOf texture.Format Is String AndAlso CStr(texture.Format) = Format AndAlso texture.Blocks.Count = 1 AndAlso texture.Blocks(0).Length = BlockSize) Then
                Throw New IOException("Unexpected colour palette " & If(texture.Id, "(no id)") & ".")
            End If
        Next
        Return found
    End Function

    ' Every TEXTURE in the PSSG file, in file order, with the image blocks beneath it.
    Public Shared Function Palettes(data As Byte()) As List(Of Palette)
        If data Is Nothing OrElse data.Length < 16 OrElse data.Length > MaximumSize OrElse data(0) <> AscW("P"c) OrElse data(1) <> AscW("S"c) OrElse data(2) <> AscW("S"c) OrElse data(3) <> AscW("G"c) Then
            Throw New IOException("Not a colour palette file.")
        End If
        If Number(data, 4) <> data.Length - 8 Then Throw New IOException("Not a colour palette file: its size doesn't match.")
        ' The schema: node names and attribute names by id.
        Dim nodes As New Dictionary(Of Long, String), attributes As New Dictionary(Of Long, String)
        Dim offset As Long = 16
        Dim nodeCount = Number(data, 12)
        For node As Long = 1 To nodeCount
            Dim id = Number(data, offset), length = Number(data, offset + 4)
            nodes(id) = Ascii(data, offset + 8, length)
            offset += 8 + length
            Dim count = Number(data, offset)
            offset += 4
            For attribute As Long = 1 To count
                Dim attributeId = Number(data, offset), attributeLength = Number(data, offset + 4)
                attributes(attributeId) = Ascii(data, offset + 8, attributeLength)
                offset += 8 + attributeLength
            Next
        Next
        Dim found As New List(Of Palette)
        Walk(data, nodes, attributes, offset, data.Length, Nothing, 0, found)
        Return found
    End Function

    Private Shared Sub Walk(data As Byte(), nodes As Dictionary(Of Long, String), attributes As Dictionary(Of Long, String), start As Long, [end] As Long, texture As Palette, depth As Integer, found As List(Of Palette))
        If depth > MaximumDepth Then Throw New IOException("Not a colour palette file: its tree is too deep.")
        Dim cursor = start
        While cursor < [end]
            Dim id = Number(data, cursor), size = Number(data, cursor + 4), attributeSize = Number(data, cursor + 8)
            Dim body = cursor + 12 + attributeSize, [stop] = cursor + 8 + size
            If [stop] > [end] OrElse body > [stop] Then Throw New IOException("Not a colour palette file: a node lies outside its parent.")
            Dim name As String = Nothing
            nodes.TryGetValue(id, name)
            Dim values As New Dictionary(Of String, Object)(StringComparer.Ordinal)
            Dim position = cursor + 12
            While position < body
                Dim attributeId = Number(data, position), length = Number(data, position + 4)
                If position + 8 + length > body Then Throw New IOException("Not a colour palette file: an attribute lies outside its node.")
                Dim attributeName As String = Nothing
                attributes.TryGetValue(attributeId, attributeName)
                Dim value As Object
                If length = 4 Then
                    value = CUInt(Number(data, position + 8))
                ElseIf length > 4 AndAlso Number(data, position + 8) = length - 4 Then
                    value = Text.Encoding.ASCII.GetString(data, CInt(position + 12), CInt(length - 4))
                Else
                    value = data.AsSpan(CInt(position + 8), CInt(length)).ToArray()
                End If
                values(If(attributeName, "")) = value
                position += 8 + length
            End While
            If name = "TEXTURE" Then
                texture = New Palette With {.Id = TryCast(Lookup(values, "id"), String), .Width = Lookup(values, "width"), .Height = Lookup(values, "height"), .Format = Lookup(values, "texelFormat")}
                found.Add(texture)
            End If
            If name = "TEXTUREIMAGEBLOCKDATA" Then
                If texture Is Nothing Then Throw New IOException("Not a colour palette file: image data outside a texture.")
                texture.Blocks.Add((CInt(body), CInt([stop] - body)))
            Else
                Walk(data, nodes, attributes, body, [stop], texture, depth + 1, found)
            End If
            cursor = [stop]
        End While
    End Sub

    Private Shared Function Lookup(values As Dictionary(Of String, Object), name As String) As Object
        Dim value As Object = Nothing
        Return If(values.TryGetValue(name, value), value, Nothing)
    End Function

    ' A big-endian 32-bit number; outside the file is refused.
    Private Shared Function Number(data As Byte(), offset As Long) As Long
        If offset < 0 OrElse offset + 4 > data.Length Then Throw New IOException("Not a colour palette file: it ends early.")
        Return BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(CInt(offset), 4))
    End Function

    Private Shared Function Ascii(data As Byte(), offset As Long, length As Long) As String
        If offset < 0 OrElse length < 0 OrElse offset + length > data.Length Then Throw New IOException("Not a colour palette file: it ends early.")
        For index = offset To offset + length - 1
            If data(CInt(index)) > 127 Then Throw New IOException("Not a colour palette file: a name isn't ASCII.")
        Next
        Return Text.Encoding.ASCII.GetString(data, CInt(offset), CInt(length))
    End Function
End Class
