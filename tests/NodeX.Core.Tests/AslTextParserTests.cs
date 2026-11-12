using NodeX.Core;

namespace NodeX.Core.Tests;

public class AslTextParserTests
{
    [Fact]
    public void Parse_NewKeyHandshake_RecognizesHandshake()
    {
        var msg = AslTextParser.Parse("!NEWKEY1!\0");
        Assert.Equal(AslTextMessageKind.Handshake, msg.Kind);
        Assert.Empty(msg.LinkedNodes);
    }

    [Fact]
    public void Parse_LinkList_ExtractsNodesAndModes_AndDeduplicates()
    {
        var msg = AslTextParser.Parse("L T47923,R1997,C1996,T47923\0");
        Assert.Equal(AslTextMessageKind.LinkList, msg.Kind);
        Assert.Equal(3, msg.LinkedNodes.Count);
        Assert.Equal(new AslRemoteLink("47923", AslRemoteLinkMode.Transceive), msg.LinkedNodes[0]);
        Assert.Equal(new AslRemoteLink("1997", AslRemoteLinkMode.Monitor), msg.LinkedNodes[1]);
        Assert.Equal(new AslRemoteLink("1996", AslRemoteLinkMode.Connecting), msg.LinkedNodes[2]);
    }

    [Fact]
    public void Parse_EmptyLinkList_ReturnsEmptyList()
    {
        var msg = AslTextParser.Parse("L \0");
        Assert.Equal(AslTextMessageKind.LinkList, msg.Kind);
        Assert.Empty(msg.LinkedNodes);
    }

    [Fact]
    public void Parse_StatusFrame_ExtractsSourceNodeKeyStateAndLinks()
    {
        var msg = AslTextParser.Parse("T 523800 STATUS,523800,0,T523802,T47923,T1997,T1996\0");
        Assert.Equal(AslTextMessageKind.Status, msg.Kind);
        Assert.Equal("523800", msg.SourceNode);
        Assert.False(msg.IsKeyed);
        Assert.Equal(["523802", "47923", "1997", "1996"], msg.LinkedNodes.Select(l => l.NodeNumber));
    }

    [Fact]
    public void FormatLinkedNodes_ExcludesLocalNode_AndCapsLongLists()
    {
        var msg = AslTextParser.Parse("L T523802,T47923,R1997,C1996,T2001,T2002,T2003");
        string summary = AslTextParser.FormatLinkedNodes(msg.LinkedNodes, excludeNode: "523802", maxShown: 4);
        Assert.Equal("47923, 1997 (RX), 1996 (…), 2001 (+2 more)", summary);
    }
}
