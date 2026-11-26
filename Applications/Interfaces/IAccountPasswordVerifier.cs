namespace Applications.Interfaces;

public enum PasswordCheckResult
{
    Verified,
    Incorrect,
    Unavailable,
}

/// <summary>
/// Checks a staff member's Portal account password against the account authentication service.
/// The password is only forwarded for the check; it is never stored or logged.
/// </summary>
public interface IAccountPasswordVerifier
{
    Task<PasswordCheckResult> VerifyAsync(string email, string password, CancellationToken cancellationToken);
}
