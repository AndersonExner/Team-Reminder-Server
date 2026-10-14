namespace TeamReminder.Server;

public enum GroupRole { Member = 0, Administrator = 1, Owner = 2 }
public enum InvitationStatus { Pending = 0, Accepted = 1, Declined = 2, Canceled = 3, Expired = 4 }

public sealed class TeamGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public Guid OwnerAccountId { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}
public sealed class GroupMembership
{
    public Guid GroupId { get; set; }
    public Guid AccountId { get; set; }
    public GroupRole Role { get; set; }
    public DateTimeOffset JoinedAt { get; set; }
}
public sealed class GroupInvitation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GroupId { get; set; }
    public Guid RecipientAccountId { get; set; }
    public Guid InviterAccountId { get; set; }
    public InvitationStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
public sealed class GroupAuditEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GroupId { get; set; }
    public Guid ActorAccountId { get; set; }
    public Guid? TargetAccountId { get; set; }
    public string Action { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}
public sealed record GroupCreateRequest(string? Name, string? Description);
public sealed record GroupEditRequest(string? Name, string? Description, long ExpectedRevision);
public sealed record GroupRevisionRequest(long ExpectedRevision);
public sealed record GroupInviteRequest(string? Email, long ExpectedRevision);
public sealed record GroupRoleRequest(GroupRole Role, long ExpectedRevision);
public sealed record GroupTransferRequest(Guid AccountId, long ExpectedRevision);
public sealed record GroupSummary(Guid Id, string Name, string Description, Guid OwnerAccountId, GroupRole MyRole, long Revision, int MemberCount);
public sealed record GroupMemberResponse(Guid AccountId, string Name, string Email, GroupRole Role, DateTimeOffset JoinedAt);
public sealed record GroupInvitationResponse(Guid Id, Guid GroupId, string GroupName, string Email, string InvitedByName, DateTimeOffset ExpiresAt, string Status);
public sealed record GroupDetails(GroupSummary Group, IReadOnlyList<GroupMemberResponse> Members, IReadOnlyList<GroupInvitationResponse> Invitations);
public sealed record GroupAuditResponse(Guid Id, Guid ActorAccountId, string ActorName, Guid? TargetAccountId, string Action, DateTimeOffset CreatedAt);
