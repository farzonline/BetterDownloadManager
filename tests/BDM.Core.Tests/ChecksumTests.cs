using System.Security.Cryptography;
using BDM.Core.Files;

namespace BDM.Core.Tests;

public class ChecksumTests
{
    static string Abc()
    {
        var p = Path.Combine(Directory.CreateTempSubdirectory().FullName, "abc.txt");
        File.WriteAllText(p, "abc");
        return p;
    }

    [Fact]
    public async Task Sha256_OfKnownBytes() =>
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            await Checksum.ComputeAsync(Abc(), HashAlgorithmName.SHA256));

    [Fact]
    public async Task Md5_OfKnownBytes() =>
        Assert.Equal("900150983cd24fb0d6963f7d28e17f72", await Checksum.ComputeAsync(Abc(), HashAlgorithmName.MD5));
}
