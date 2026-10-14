using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace TeamReminder.Server;

public static class GroupEndpoints
{
    private static Guid Actor(ClaimsPrincipal principal) => Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private static IResult Message(int status, string message) => Results.Json(new { message }, statusCode: status);
    private static Dictionary<string, string[]> Validate(string? name, string? description)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100 || name.Any(char.IsControl))
            errors["name"] = ["Informe um nome de 1 a 100 caracteres, em uma linha."];
        if (description?.Trim().Length > 500) errors["description"] = ["Use uma descrição de até 500 caracteres."];
        return errors;
    }
    private static GroupSummary Summary(TeamGroup team, GroupRole role, int count) =>
        new(team.Id, team.Name, team.Description, team.OwnerAccountId, role, team.Revision, count);
    private static void Audit(AuthDb db, TeamGroup team, Guid actor, string action, DateTimeOffset now, Guid? target = null) =>
        db.GroupAuditEvents.Add(new() { GroupId = team.Id, ActorAccountId = actor, TargetAccountId = target, Action = action, CreatedAt = now });

    public static void MapGroups(this WebApplication app)
    {
        var groups = app.MapGroup("/api/groups").RequireAuthorization().RequireRateLimiting("groups");
        groups.MapGet("/", async (ClaimsPrincipal principal, AuthDb db, CancellationToken ct) =>
        {
            var actor = Actor(principal);
            var rows = await (from member in db.GroupMemberships join team in db.Groups on member.GroupId equals team.Id
                where member.AccountId == actor && team.DeletedAt == null && team.IsActive orderby team.Name
                select new { Group = team, member.Role, Count = db.GroupMemberships.Count(x => x.GroupId == team.Id) }).ToListAsync(ct);
            return Results.Ok(rows.Select(x => Summary(x.Group, x.Role, x.Count)));
        });
        groups.MapPost("/", async (GroupCreateRequest request, ClaimsPrincipal principal, AuthDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var errors = Validate(request.Name, request.Description);
            if (errors.Count != 0) return Results.ValidationProblem(errors);
            var actor = Actor(principal);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Accounts.FromSqlInterpolated($"SELECT * FROM \"Accounts\" WHERE \"Id\" = {actor} FOR NO KEY UPDATE").ToListAsync(ct);
            if (!await db.Accounts.AnyAsync(x=>x.Id==actor&&x.IsActive&&x.DeletedAt==null,ct)) return Results.Unauthorized();
            if (await db.Groups.CountAsync(x => x.OwnerAccountId == actor && x.DeletedAt == null, ct) >= 20)
                return Message(422, "Você pode ser proprietário de até 20 grupos.");
            if (await MembershipCount(db, actor, ct) >= 100) return Message(422, "Você pode participar de até 100 grupos.");
            var team = new TeamGroup { Name = request.Name!.Trim(), Description = request.Description?.Trim() ?? "", OwnerAccountId = actor, CreatedAt = clock.GetUtcNow() };
            db.Groups.Add(team);
            db.GroupMemberships.Add(new() { GroupId = team.Id, AccountId = actor, Role = GroupRole.Owner, JoinedAt = team.CreatedAt });
            Audit(db, team, actor, "Created", team.CreatedAt);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return Results.Created($"/api/groups/{team.Id}", Summary(team, GroupRole.Owner, 1));
        });
        groups.MapGet("/{id:guid}", DetailsAsync);
        groups.MapPatch("/{id:guid}", (Guid id, GroupEditRequest request, ClaimsPrincipal principal, AuthDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var errors = Validate(request.Name, request.Description);
            if (errors.Count != 0) return Task.FromResult<IResult>(Results.ValidationProblem(errors));
            return WithGroup(id, request.ExpectedRevision, principal, db, ct, (team, actor, role) =>
            {
                if (role == GroupRole.Member) return Task.FromResult<IResult>(Results.Forbid());
                team.Name = request.Name!.Trim(); team.Description = request.Description?.Trim() ?? "";
                Audit(db, team, actor, "Updated", clock.GetUtcNow());
                return Task.FromResult<IResult>(Results.NoContent());
            });
        });
        groups.MapDelete("/{id:guid}", (Guid id, long revision, ClaimsPrincipal principal, AuthDb db, TimeProvider clock, CancellationToken ct) =>
            WithGroup(id, revision, principal, db, ct, async (team, actor, role) =>
            {
                if (role != GroupRole.Owner) return Results.Forbid();
                team.DeletedAt = clock.GetUtcNow();
                await CancelInvitations(db, id, null, ct);
                Audit(db, team, actor, "Deleted", clock.GetUtcNow());
                return Results.NoContent();
            }));
        groups.MapPost("/{id:guid}/leave", (Guid id, GroupRevisionRequest request, ClaimsPrincipal principal, AuthDb db, TimeProvider clock, CancellationToken ct) =>
            WithGroup(id, request.ExpectedRevision, principal, db, ct, async (team, actor, role) =>
            {
                if (role == GroupRole.Owner) return Message(409, "Transfira a propriedade antes de sair do grupo.");
                db.GroupMemberships.Remove(await db.GroupMemberships.SingleAsync(x => x.GroupId == id && x.AccountId == actor, ct));
                await CancelInvitations(db, id, actor, ct);
                Audit(db, team, actor, "Left", clock.GetUtcNow(), actor);
                return Results.NoContent();
            }));
        groups.MapPost("/{id:guid}/transfer", (Guid id, GroupTransferRequest request, ClaimsPrincipal principal, AuthDb db, TimeProvider clock, CancellationToken ct) =>
            WithGroup(id, request.ExpectedRevision, principal, db, ct, async (team, actor, role) =>
            {
                if (role != GroupRole.Owner) return Results.Forbid();
                if (request.AccountId == actor) return Message(409, "Escolha outro participante para assumir o grupo.");
                var next = await db.GroupMemberships.SingleOrDefaultAsync(x => x.GroupId == id && x.AccountId == request.AccountId, ct);
                if (next is null) return Results.NotFound();
                await db.Accounts.FromSqlInterpolated($"SELECT * FROM \"Accounts\" WHERE \"Id\" = {request.AccountId} FOR NO KEY UPDATE").ToListAsync(ct);
                if(!await db.Accounts.AnyAsync(x=>x.Id==request.AccountId&&x.IsActive&&x.DeletedAt==null,ct)) return Message(409,"O novo proprietário está desativado.");
                if (await db.Groups.CountAsync(x => x.OwnerAccountId == request.AccountId && x.DeletedAt == null, ct) >= 20)
                    return Message(422, "O participante já é proprietário de 20 grupos.");
                var previous = await db.GroupMemberships.SingleAsync(x => x.GroupId == id && x.AccountId == actor, ct);
                previous.Role = GroupRole.Administrator; next.Role = GroupRole.Owner; team.OwnerAccountId = request.AccountId;
                Audit(db, team, actor, "OwnershipTransferred", clock.GetUtcNow(), request.AccountId);
                return Results.NoContent();
            }));
        groups.MapPatch("/{id:guid}/members/{accountId:guid}", (Guid id, Guid accountId, GroupRoleRequest request, ClaimsPrincipal principal, AuthDb db, TimeProvider clock, CancellationToken ct) =>
            WithGroup(id, request.ExpectedRevision, principal, db, ct, async (team, actor, role) =>
            {
                if (role == GroupRole.Member) return Results.Forbid();
                if (request.Role is not (GroupRole.Member or GroupRole.Administrator)) return Message(400, "Use membro ou administrador. Propriedade exige transferência.");
                var member = await db.GroupMemberships.SingleOrDefaultAsync(x => x.GroupId == id && x.AccountId == accountId, ct);
                if (member is null) return Results.NotFound();
                if (member.Role == GroupRole.Owner) return Message(409, "O proprietário não pode ser rebaixado.");
                member.Role = request.Role;
                if (request.Role == GroupRole.Member) await CancelInvitations(db, id, accountId, ct);
                Audit(db, team, actor, request.Role == GroupRole.Administrator ? "Promoted" : "Demoted", clock.GetUtcNow(), accountId);
                return Results.NoContent();
            }));
        groups.MapDelete("/{id:guid}/members/{accountId:guid}", (Guid id, Guid accountId, long revision, ClaimsPrincipal principal, AuthDb db, TimeProvider clock, CancellationToken ct) =>
            WithGroup(id, revision, principal, db, ct, async (team, actor, role) =>
            {
                if (role == GroupRole.Member) return Results.Forbid();
                var member = await db.GroupMemberships.SingleOrDefaultAsync(x => x.GroupId == id && x.AccountId == accountId, ct);
                if (member is null) return Results.NotFound();
                if (member.Role == GroupRole.Owner) return Message(409, "O proprietário não pode ser removido.");
                db.GroupMemberships.Remove(member); await CancelInvitations(db, id, accountId, ct);
                Audit(db, team, actor, "Removed", clock.GetUtcNow(), accountId);
                return Results.NoContent();
            }));
        groups.MapPost("/{id:guid}/invitations", (Guid id, GroupInviteRequest request, ClaimsPrincipal principal, AuthDb db, TimeProvider clock, CancellationToken ct) =>
            WithGroup(id, request.ExpectedRevision, principal, db, ct, async (team, actor, role) =>
            {
                if (role == GroupRole.Member) return Results.Forbid();
                if (!RegistrationValidation.ValidEmail(request.Email)) return Message(400, "Informe um e-mail válido.");
                var normalized = request.Email!.Trim().ToUpperInvariant();
                var recipient = await db.Accounts.SingleOrDefaultAsync(x => x.NormalizedEmail == normalized && x.IsActive && x.DeletedAt == null, ct);
                if (recipient is null) return Message(404, "Não foi possível convidar: o e-mail deve ter uma conta cadastrada.");
                if (await db.GroupMemberships.AnyAsync(x => x.GroupId == id && x.AccountId == recipient.Id, ct)) return Message(409, "Esta pessoa já participa do grupo.");
                await db.Accounts.FromSqlInterpolated($"SELECT * FROM \"Accounts\" WHERE \"Id\" = {recipient.Id} FOR NO KEY UPDATE").ToListAsync(ct);
                var now = clock.GetUtcNow();
                await db.Entry(recipient).ReloadAsync(ct);
                if(!recipient.IsActive||recipient.DeletedAt!=null)return Message(409,"A conta está desativada.");
                if (await db.GroupInvitations.CountAsync(x => x.RecipientAccountId == recipient.Id && x.Status == InvitationStatus.Pending && x.ExpiresAt > now, ct) >= 100)
                    return Message(422, "Esta pessoa já tem 100 convites pendentes.");
                if (await db.GroupInvitations.AnyAsync(x => x.GroupId == id && x.RecipientAccountId == recipient.Id && x.Status == InvitationStatus.Pending && x.ExpiresAt > now, ct))
                    return Message(409, "Já existe um convite pendente para esta pessoa.");
                if (await db.GroupInvitations.CountAsync(x => x.GroupId == id && x.Status == InvitationStatus.Pending && x.ExpiresAt > now, ct) >= 100)
                    return Message(422, "O grupo pode ter até 100 convites pendentes.");
                var invite = new GroupInvitation { GroupId = id, RecipientAccountId = recipient.Id, InviterAccountId = actor, CreatedAt = now, ExpiresAt = now.AddDays(7) };
                db.GroupInvitations.Add(invite); Audit(db, team, actor, "Invited", now, recipient.Id);
                var inviter = await db.Accounts.SingleAsync(x => x.Id == actor, ct);
                return Results.Created($"/api/invitations/{invite.Id}", new GroupInvitationResponse(invite.Id, id, team.Name, recipient.Email, inviter.Name, invite.ExpiresAt, "Pending"));
            }));
        groups.MapPost("/{id:guid}/invitations/{invitationId:guid}/cancel", (Guid id, Guid invitationId, GroupRevisionRequest request, ClaimsPrincipal principal, AuthDb db, TimeProvider clock, CancellationToken ct) =>
            WithGroup(id, request.ExpectedRevision, principal, db, ct, async (team, actor, role) =>
            {
                if (role == GroupRole.Member) return Results.Forbid();
                var invite = await db.GroupInvitations.SingleOrDefaultAsync(x => x.Id == invitationId && x.GroupId == id, ct);
                if (invite is null) return Results.NotFound();
                if (invite.Status != InvitationStatus.Pending) return Message(409, "Este convite não está mais pendente.");
                invite.Status = InvitationStatus.Canceled; Audit(db, team, actor, "InvitationCanceled", clock.GetUtcNow(), invite.RecipientAccountId);
                return Results.NoContent();
            }));
        groups.MapGet("/{id:guid}/audit", async (Guid id, ClaimsPrincipal principal, AuthDb db, CancellationToken ct) =>
        {
            var member = await FindMember(db, id, Actor(principal), ct);
            if (member is null) return Results.NotFound();
            if (member.Role == GroupRole.Member) return Results.Forbid();
            var events = await (from entry in db.GroupAuditEvents join account in db.Accounts on entry.ActorAccountId equals account.Id
                where entry.GroupId == id orderby entry.CreatedAt descending, entry.Id descending
                select new GroupAuditResponse(entry.Id, entry.ActorAccountId, account.Name, entry.TargetAccountId, entry.Action, entry.CreatedAt)).Take(50).ToListAsync(ct);
            return Results.Ok(events);
        });
        var invitations = app.MapGroup("/api/invitations").RequireAuthorization().RequireRateLimiting("groups");
        invitations.MapGet("/", async (ClaimsPrincipal principal, AuthDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var actor = Actor(principal); var now = clock.GetUtcNow();
            return Results.Ok(await PendingInvites(db, null, actor, now, ct));
        });
        invitations.MapPost("/{id:guid}/accept", (Guid id, ClaimsPrincipal principal, AuthDb db, TimeProvider clock, CancellationToken ct) => Respond(id, true, principal, db, clock, ct));
        invitations.MapPost("/{id:guid}/decline", (Guid id, ClaimsPrincipal principal, AuthDb db, TimeProvider clock, CancellationToken ct) => Respond(id, false, principal, db, clock, ct));
    }

    private static Task<List<GroupInvitationResponse>> PendingInvites(AuthDb db, Guid? groupId, Guid? recipientId, DateTimeOffset now, CancellationToken ct) =>
        (from invite in db.GroupInvitations join team in db.Groups on invite.GroupId equals team.Id
        join recipient in db.Accounts on invite.RecipientAccountId equals recipient.Id
        join inviter in db.Accounts on invite.InviterAccountId equals inviter.Id
        where team.DeletedAt == null && team.IsActive && invite.Status == InvitationStatus.Pending && invite.ExpiresAt > now
            && (groupId == null || invite.GroupId == groupId) && (recipientId == null || invite.RecipientAccountId == recipientId)
        orderby invite.ExpiresAt
        select new GroupInvitationResponse(invite.Id, team.Id, team.Name, recipient.Email, inviter.Name, invite.ExpiresAt, "Pending")).ToListAsync(ct);

    private static Task<GroupMembership?> FindMember(AuthDb db, Guid id, Guid actor, CancellationToken ct) =>
        (from member in db.GroupMemberships join team in db.Groups on member.GroupId equals team.Id
         where member.GroupId == id && member.AccountId == actor && team.DeletedAt == null && team.IsActive select member).SingleOrDefaultAsync(ct);
    private static Task<int> MembershipCount(AuthDb db, Guid actor, CancellationToken ct) =>
        (from member in db.GroupMemberships join team in db.Groups on member.GroupId equals team.Id
         where member.AccountId == actor && team.DeletedAt == null && team.IsActive select member).CountAsync(ct);

    private static async Task<IResult> DetailsAsync(Guid id, ClaimsPrincipal principal, AuthDb db, TimeProvider clock, CancellationToken ct)
    {
        var member = await FindMember(db, id, Actor(principal), ct);
        if (member is null) return Results.NotFound();
        var team = await db.Groups.SingleAsync(x => x.Id == id, ct);
        var members = await (from membership in db.GroupMemberships join account in db.Accounts on membership.AccountId equals account.Id
            where membership.GroupId == id orderby membership.Role descending, account.Name
            select new GroupMemberResponse(account.Id, account.Name, account.Email, membership.Role, membership.JoinedAt)).ToListAsync(ct);
        List<GroupInvitationResponse> invites = member.Role == GroupRole.Member ? [] : await PendingInvites(db, id, null, clock.GetUtcNow(), ct);
        return Results.Ok(new GroupDetails(Summary(team, member.Role, members.Count), members, invites));
    }

    private static async Task<IResult> WithGroup(Guid id, long revision, ClaimsPrincipal principal, AuthDb db, CancellationToken ct,
        Func<TeamGroup, Guid, GroupRole, Task<IResult>> action)
    {
        var actor = Actor(principal);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var rows = await db.Groups.FromSqlInterpolated($"SELECT * FROM \"Groups\" WHERE \"Id\" = {id} FOR UPDATE").ToListAsync(ct);
        var team = rows.SingleOrDefault();
        if (team is null || team.DeletedAt is not null || !team.IsActive) return Results.NotFound();
        var member = await db.GroupMemberships.SingleOrDefaultAsync(x => x.GroupId == id && x.AccountId == actor, ct);
        if (member is null) return Results.NotFound();
        if (revision != team.Revision) return Message(409, "O grupo mudou. Atualize a lista e tente novamente.");
        team.Revision++;
        var result = await action(team, actor, member.Role);
        if (result is not IStatusCodeHttpResult { StatusCode: >= 200 and < 300 }) return result;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return result;
    }

    private static Task<int> CancelInvitations(AuthDb db, Guid groupId, Guid? inviter, CancellationToken ct) =>
        db.GroupInvitations.Where(x => x.GroupId == groupId && x.Status == InvitationStatus.Pending && (inviter == null || x.InviterAccountId == inviter))
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, InvitationStatus.Canceled), ct);

    private static async Task<IResult> Respond(Guid id, bool accept, ClaimsPrincipal principal, AuthDb db, TimeProvider clock, CancellationToken ct)
    {
        var actor = Actor(principal);
        var lookup = await db.GroupInvitations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.RecipientAccountId == actor, ct);
        if (lookup is null) return Results.NotFound();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var rows = await db.Groups.FromSqlInterpolated($"SELECT * FROM \"Groups\" WHERE \"Id\" = {lookup.GroupId} FOR UPDATE").ToListAsync(ct);
        var team = rows.SingleOrDefault();
        if (team is null || team.DeletedAt is not null || !team.IsActive) return Results.NotFound();
        var invite = await db.GroupInvitations.SingleAsync(x => x.Id == id, ct);
        var exists = await db.GroupMemberships.AnyAsync(x => x.GroupId == team.Id && x.AccountId == actor, ct);
        if ((accept && invite.Status == InvitationStatus.Accepted && exists) || (!accept && invite.Status == InvitationStatus.Declined)) return Results.NoContent();
        if (invite.Status != InvitationStatus.Pending) return Message(409, "Este convite não está mais pendente.");
        var now = clock.GetUtcNow();
        if (invite.ExpiresAt <= now) return Message(410, "O convite expirou. Peça um novo convite ao administrador.");
        if (accept)
        {
            await db.Accounts.FromSqlInterpolated($"SELECT * FROM \"Accounts\" WHERE \"Id\" = {actor} FOR NO KEY UPDATE").ToListAsync(ct);
            if(!await db.Accounts.AnyAsync(x=>x.Id==actor&&x.IsActive&&x.DeletedAt==null,ct))return Results.Unauthorized();
            await db.Entry(invite).ReloadAsync(ct);
            if(invite.Status!=InvitationStatus.Pending)return Message(409,"Este convite não está mais pendente.");
            if (await db.GroupMemberships.CountAsync(x => x.GroupId == team.Id, ct) >= 100) return Message(422, "O grupo atingiu 100 participantes.");
            if (await MembershipCount(db, actor, ct) >= 100) return Message(422, "Você atingiu 100 grupos.");
            if (!exists) db.GroupMemberships.Add(new() { GroupId = team.Id, AccountId = actor, Role = GroupRole.Member, JoinedAt = now });
        }
        invite.Status = accept ? InvitationStatus.Accepted : InvitationStatus.Declined;
        team.Revision++; Audit(db, team, actor, accept ? "InvitationAccepted" : "InvitationDeclined", now, actor);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return Results.NoContent();
    }
}
