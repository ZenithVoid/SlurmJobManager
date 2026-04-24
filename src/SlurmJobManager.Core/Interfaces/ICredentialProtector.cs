namespace SlurmJobManager.Core.Interfaces;

/// <summary>Encrypts and decrypts sensitive credential strings.</summary>
public interface ICredentialProtector
{
    /// <summary>Encrypts plain-text to a Base-64 cipher string.</summary>
    string Protect(string plainText);

    /// <summary>Decrypts a previously protected cipher string back to plain text.</summary>
    /// <exception cref="InvalidOperationException">Thrown when decryption fails.</exception>
    string Unprotect(string cipherText);
}
