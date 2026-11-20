using System;

namespace Domains.Entities;

/// <summary>
/// Storage work owed for one proof File after POS committed it. Written in the same commit as the
/// delivery confirmation or refund request, so it survives a restart, and its unique File ID makes
/// that commit the once-only consumption of the upload reference.
///
/// Attach: Pending → (link added) Linked → (made permanent and verified) Done.
/// When a later refund photo replaces this one, the task switches to Detach and, once the newer
/// photo is attached, removes this File's exact order link → Retired.
/// </summary>
public class ProofFileTask
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    /// <summary>"delivery" or "refund".</summary>
    public string Kind { get; set; } = string.Empty;
    public Guid FileId { get; set; }
    public string Stage { get; set; } = ProofFileTaskStages.Pending;
    public int Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; } = DateTime.UtcNow;
    /// <summary>When storage expires the still-temporary File; attach retries stop after this.</summary>
    public DateTime? FileExpiresAt { get; set; }
    /// <summary>Error category only; never a signed URL or credential.</summary>
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}

public static class ProofFileTaskStages
{
    public const string Pending = "Pending";
    public const string Linked = "Linked";
    public const string Done = "Done";
    /// <summary>Replaced by a newer photo: remove this File's order link after the newer one is attached.</summary>
    public const string Detach = "Detach";
    public const string Retired = "Retired";
    /// <summary>The temporary File expired before it could be attached.</summary>
    public const string Expired = "Expired";

    public static bool IsOpen(string stage) => stage is Pending or Linked or Detach;
    public static bool IsAttaching(string stage) => stage is Pending or Linked;
}
