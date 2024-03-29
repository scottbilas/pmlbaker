using System;
using System.Linq;
using System.Text.RegularExpressions;
using NiceIO;
using NUnit.Framework;
using ProcMonUtils;
using Shouldly;

// ReSharper disable StringLiteralTypo

class Tests
{
    NPath m_PmlPath = null!, m_PmipPath = null!, m_PmlBakedPath = null!; 
    
    // crap tests just to get some basic sanity..
    
    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var testDataPath = TestContext.CurrentContext
            .TestDirectory.ToNPath()
            .ParentContaining("testdata", true)
            .DirectoryMustExist();

        m_PmlPath = testDataPath.Combine("basic.pml");
        m_PmipPath = testDataPath.Files("pmip*.txt").Single();
        m_PmlBakedPath = m_PmlPath.ChangeExtension(".pmlbaked");
    }
    
    void Symbolicate()
    {
        PmlUtils.Symbolicate(m_PmlPath, new SymbolicateOptions
        {
            MonoPmipPaths = new[] { m_PmipPath.ToString() },
            BakedPath = m_PmlBakedPath,
            NtSymbolPath = "",
        });
    }

    [Test]
    public void PmipBasics()
    {
        var mono = new MonoSymbolReader(m_PmipPath);
        foreach (var symbol in mono.Symbols)
        {
            mono.TryFindSymbol(symbol.Address.Base + symbol.Address.Size / 2, out var sym0).ShouldBeTrue();
            sym0.ShouldBe(symbol);
            
            mono.TryFindSymbol(symbol.Address.Base, out var sym1).ShouldBeTrue();
            sym1.ShouldBe(symbol);
            
            mono.TryFindSymbol(symbol.Address.End - 1, out var sym2).ShouldBeTrue();
            sym2.ShouldBe(symbol);
        }
        
        mono.TryFindSymbol(mono.Symbols[0].Address.Base - 1, out _).ShouldBeFalse();
        mono.TryFindSymbol(mono.Symbols[^1].Address.End, out _).ShouldBeFalse();
    }
    
    [Test]
    public void WriteAndParse()
    {
        Symbolicate();

        var pmlQuery = new PmlQuery(m_PmlBakedPath);
        
        var frame = pmlQuery.GetRecordBySequence(36).Frames[2];
        pmlQuery.GetString(frame.ModuleStringIndex).ShouldBe("FLTMGR.SYS");
        frame.Type.ShouldBe(FrameType.Kernel);
        pmlQuery.GetString(frame.SymbolStringIndex).ShouldBe("FltGetFileNameInformation");
        
        // TODO: this is unstable; as the OS gets updated, offsets change..
        frame.Offset.ShouldBe(0x752ul);
    }
    
    [Test]
    public void Match()
    {
        Symbolicate();

        var pmlQuery = new PmlQuery(m_PmlBakedPath);
        
        // find all events where someone is calling a dotnet generic
        var matches = pmlQuery
            .MatchRecordsBySymbol(new Regex("`"))
            .OrderBy(seq => seq)
            .Select(seq => pmlQuery.GetRecordBySequence(seq))
            .ToList();
        
        matches.First().Sequence.ShouldBe(3);
        matches.Last().Sequence.ShouldBe(311);
    }
}
