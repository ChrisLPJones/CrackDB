using HoarderDB.Web.Auth;
using HoarderDB.Web.Options;
using Microsoft.AspNetCore.Identity;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace HoarderDB.Web.Tests;

/// <summary>Login stamp comes from the same snapshot as the verified hash. Throwaway Test_&lt;guid&gt; key dir only.</summary>
public class CredentialVerifierTests
{
    [Fact]
    public void VerifyLogin_ReturnsStampOfVerifiedSnapshot_EvenIfPasswordChangesMidVerify()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Test_" + Guid.NewGuid());
        HoarderDbWebFactory.EnsureThrowaway(dir);
        try
        {
            const string user = "tester", oldPw = "old test passphrase 1", newPw = "new test passphrase 2";
            var auth = new AuthOptions { Username = user, PasswordHash = new PasswordHasher<string>().HashPassword(user, oldPw) };
            var v = new CredentialVerifier(MsOptions.Create(auth), MsOptions.Create(new StorageOptions { KeyPath = dir }));
            var stampA = v.CurrentStamp;

            // The password changes after the snapshot was taken: old password still verifies, with A's stamp.
            Assert.True(v.VerifyLogin(user, oldPw, out var stamp, () => v.SetPassword(newPw)));
            Assert.Equal(stampA, stamp);
            Assert.NotEqual(stampA, v.CurrentStamp);

            // Failure path returns no stamp.
            Assert.False(v.VerifyLogin(user, oldPw, out var none));
            Assert.Null(none);
            Assert.True(v.VerifyLogin(user, newPw, out var stampB));
            Assert.Equal(v.CurrentStamp, stampB);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void SymlinkedStoreToExistingFile_IsRefused()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = Path.Combine(Path.GetTempPath(), "Test_" + Guid.NewGuid());
        HoarderDbWebFactory.EnsureThrowaway(dir);
        try
        {
            Directory.CreateDirectory(dir);
            var real = Path.Combine(dir, "elsewhere.json");
            File.WriteAllText(real, "{}");
            File.CreateSymbolicLink(Path.Combine(dir, AdminPasswordStore.FileName), real);
            var store = new AdminPasswordStore(dir);
            Assert.Throws<InvalidOperationException>(() => store.Load("env-hash", _ => true));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void DanglingStoreLink_FailsInsteadOfFallingBackToEnvHash()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = Path.Combine(Path.GetTempPath(), "Test_" + Guid.NewGuid());
        HoarderDbWebFactory.EnsureThrowaway(dir);
        try
        {
            Directory.CreateDirectory(dir);
            File.CreateSymbolicLink(Path.Combine(dir, AdminPasswordStore.FileName), Path.Combine(dir, "missing-target"));
            var auth = new AuthOptions { Username = "tester", PasswordHash = new PasswordHasher<string>().HashPassword("tester", "some test passphrase") };
            Assert.Throws<InvalidOperationException>(() =>
                new CredentialVerifier(MsOptions.Create(auth), MsOptions.Create(new StorageOptions { KeyPath = dir })));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
