using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
namespace TeamReminder.Server;

public sealed class AdminAuditEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ActorAccountId { get; set; }
    public Guid TargetId { get; set; }
    public string Entity { get; set; } = "";
    public string Action { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}
public sealed record AdminUserRequest(string? Name, string? Email, string? Password, bool IsActive, bool IsSystemAdmin, long Revision = 0);
public sealed record AdminGroupRequest(string? Name, string? Description, Guid OwnerAccountId, bool IsActive, long Revision = 0, string? OwnerEmail = null);
public sealed record AdminRevision(long Revision);
public sealed record AdminUser(Guid Id, string Name, string Email, bool IsActive, bool IsSystemAdmin, DateTimeOffset? DeletedAt, long Revision, DateTimeOffset CreatedAt);
public sealed record AdminGroup(Guid Id, string Name, string Description, Guid OwnerAccountId, bool IsActive, DateTimeOffset? DeletedAt, long Revision, DateTimeOffset CreatedAt, string? OwnerEmail = null);
public sealed record AdminPage<T>(IReadOnlyList<T> Items, long Total, int Page, int PageSize);
public sealed record AdminAuditItem(Guid Id, Guid ActorAccountId, Guid TargetId, string Entity, string Action, DateTimeOffset CreatedAt, string? ActorName, string? TargetName);

public static class AdminEndpoints
{
    private static Guid Actor(ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private static IResult Error(int code, string message) => Results.Json(new { message }, statusCode: code);
    private static AdminUser User(Account a) => new(a.Id,a.Name,a.Email,a.IsActive,a.IsSystemAdmin,a.DeletedAt,a.Revision,a.CreatedAt);
    private static AdminGroup Group(TeamGroup g) => new(g.Id,g.Name,g.Description,g.OwnerAccountId,g.IsActive,g.DeletedAt,g.Revision,g.CreatedAt);
    private static void Audit(AuthDb db, TimeProvider clock, Guid actor, Guid target, string entity, string action) =>
        db.AdminAuditEvents.Add(new() { ActorAccountId=actor, TargetId=target, Entity=entity, Action=action, CreatedAt=clock.GetUtcNow() });
    // ILIKE with escaped wildcards: searches ignore case and treat % and _ literally.
    private static string Like(string term) => "%" + term.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";
    private static Task Lock(AuthDb db, CancellationToken ct) => db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(734620261015)", ct);
    private static bool ValidGroup(AdminGroupRequest r) => !string.IsNullOrWhiteSpace(r.Name) && r.Name.Trim().Length <=100 && !r.Name.Any(char.IsControl) && (r.Description?.Length ?? 0)<=500;
    private static Dictionary<string,string[]> ValidateUser(AdminUserRequest r, bool create)
    {
        var errors=RegistrationValidation.Validate(new(r.Name,r.Email,create ? r.Password : r.Password ?? "valid placeholder"));
        return errors;
    }
    public static void MapAdministration(this WebApplication app)
    {

        var api=app.MapGroup("/api/admin").RequireAuthorization("SystemAdmin").RequireRateLimiting("groups");
        api.MapGet("/me", async (ClaimsPrincipal p, TimeProvider clock,AuthDb db, CancellationToken ct) => Results.Ok(User(await db.Accounts.SingleAsync(x=>x.Id==Actor(p),ct))));
        api.MapGet("/summary", async (TimeProvider clock,AuthDb db,CancellationToken ct) => Results.Ok(new {
            accounts=await db.Accounts.CountAsync(x=>x.DeletedAt==null,ct),
            activeUsers=await db.Accounts.CountAsync(x=>x.DeletedAt==null&&x.IsActive,ct),
            groups=await db.Groups.CountAsync(x=>x.DeletedAt==null,ct),
            activeGroups=await db.Groups.CountAsync(x=>x.DeletedAt==null&&x.IsActive,ct),
            alarms=await db.PersonalAlarms.CountAsync(x=>!x.Deleted,ct)+await db.GroupAlarms.CountAsync(x=>!x.Deleted&&db.Groups.Any(g=>g.Id==x.GroupId&&g.DeletedAt==null),ct),
            pendingInvitations=await db.GroupInvitations.CountAsync(x=>x.Status==InvitationStatus.Pending&&x.ExpiresAt>clock.GetUtcNow(),ct)
        }));
        api.MapGet("/users", async (int? page,string? q,string? status,TimeProvider clock,AuthDb db,CancellationToken ct) => {
            var number=Math.Max(page??1,1); if(number>100000)return Error(400,"Página inválida.");
            var query=db.Accounts.AsNoTracking().AsQueryable();
            if(!string.IsNullOrWhiteSpace(q)) { var term=q.Trim(); if(term.Length>254)return Error(400,"Busca muito longa."); var pattern=Like(term);query=query.Where(x=>EF.Functions.ILike(x.Name,pattern,@"\")||EF.Functions.ILike(x.Email,pattern,@"\")); }
            query=status switch { "active"=>query.Where(x=>x.IsActive&&x.DeletedAt==null), "inactive"=>query.Where(x=>!x.IsActive&&x.DeletedAt==null), "deleted"=>query.Where(x=>x.DeletedAt!=null), _=>query };
            var count=await query.LongCountAsync(ct);
            var rows=await query.OrderBy(x=>x.Name).ThenBy(x=>x.Id).Skip((number-1)*20).Take(20).ToListAsync(ct);
            return Results.Ok(new AdminPage<AdminUser>(rows.Select(User).ToArray(),count,number,20));
        });
        api.MapGet("/users/{id:guid}", async (Guid id,TimeProvider clock,AuthDb db,CancellationToken ct) => {
            var user=await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct);
            if(user is null)return Results.NotFound();
            var groups=await (from m in db.GroupMemberships join g in db.Groups on m.GroupId equals g.Id where m.AccountId==id
                select new {g.Id,g.Name,m.Role,g.IsActive,g.DeletedAt}).Take(100).ToListAsync(ct);
            var alarms=await db.PersonalAlarms.Where(x=>x.AccountId==id&&!x.Deleted).OrderByDescending(x=>x.Version)
                .Select(x=>new {x.Id,x.Title,x.ScheduledAtUtc,x.Version,x.Recurrence}).Take(100).ToListAsync(ct);
            return Results.Ok(new {user=User(user),groups,alarms,alarmCount=await db.PersonalAlarms.CountAsync(x=>x.AccountId==id&&!x.Deleted,ct)});
        });
        api.MapPost("/users", async (AdminUserRequest r,ClaimsPrincipal p,TimeProvider clock,AuthDb db,IPasswordHasher<Account> hasher,CancellationToken ct)=>{
            var errors=ValidateUser(r,true); if(errors.Count>0)return Results.ValidationProblem(errors);
            await using var tx=await db.Database.BeginTransactionAsync(ct); await Lock(db,ct);
            if(!await db.Accounts.AnyAsync(x=>x.Id==Actor(p)&&x.IsActive&&x.IsSystemAdmin&&x.DeletedAt==null,ct))return Results.Forbid();
            var normalized=r.Email!.Trim().ToUpperInvariant();
            if(await db.Accounts.AnyAsync(x=>x.NormalizedEmail==normalized,ct))return Error(409,"E-mail já cadastrado, inclusive em contas excluídas.");
            var a=new Account {Name=r.Name!.Trim(),Email=r.Email.Trim(),NormalizedEmail=normalized,IsActive=r.IsActive,IsSystemAdmin=r.IsSystemAdmin};
            a.PasswordHash=hasher.HashPassword(a,r.Password!); db.Accounts.Add(a); Audit(db,clock,Actor(p),a.Id,"User","Created");
            try {await db.SaveChangesAsync(ct);}catch(DbUpdateException ex)when(ex.InnerException is PostgresException {SqlState:PostgresErrorCodes.UniqueViolation}){return Error(409,"E-mail já cadastrado.");}
            await tx.CommitAsync(ct); return Results.Created($"/api/admin/users/{a.Id}",User(a));
        });
        api.MapPatch("/users/{id:guid}",async (Guid id,AdminUserRequest r,ClaimsPrincipal p,TimeProvider clock,AuthDb db,IPasswordHasher<Account> hasher,CancellationToken ct)=>{
            var errors=ValidateUser(r,false);if(errors.Count>0)return Results.ValidationProblem(errors);
            await using var tx=await db.Database.BeginTransactionAsync(ct);await Lock(db,ct);
            if(!await db.Accounts.AnyAsync(x=>x.Id==Actor(p)&&x.IsActive&&x.IsSystemAdmin&&x.DeletedAt==null,ct))return Results.Forbid();
            var a=await db.Accounts.FromSqlInterpolated($"SELECT * FROM \"Accounts\" WHERE \"Id\"={id} FOR NO KEY UPDATE").SingleOrDefaultAsync(ct);
            if(a is null)return Results.NotFound();if(a.DeletedAt!=null)return Error(409,"Restaure a conta antes de editar.");
            if(a.Revision!=r.Revision)return Error(409,"Registro alterado. Atualize a lista.");
            if(id==Actor(p)&&(!r.IsActive||!r.IsSystemAdmin))return Error(409,"Você não pode desativar ou retirar seu próprio acesso administrativo.");
            if(a.IsSystemAdmin&&a.IsActive&&(!r.IsSystemAdmin||!r.IsActive)&&await db.Accounts.CountAsync(x=>x.IsSystemAdmin&&x.IsActive&&x.DeletedAt==null,ct)<=1)
                return Error(409,"Mantenha ao menos um administrador ativo.");
            var normalized=r.Email!.Trim().ToUpperInvariant();
            if(await db.Accounts.AnyAsync(x=>x.Id!=id&&x.NormalizedEmail==normalized,ct))return Error(409,"E-mail já cadastrado.");
            var revoke=a.IsActive!=r.IsActive||a.IsSystemAdmin!=r.IsSystemAdmin||a.Email!=r.Email.Trim()||r.Password!=null;
            a.Name=r.Name!.Trim();a.Email=r.Email.Trim();a.NormalizedEmail=normalized;a.IsActive=r.IsActive;a.IsSystemAdmin=r.IsSystemAdmin;a.Revision++;
            if(r.Password!=null)a.PasswordHash=hasher.HashPassword(a,r.Password);
            if(revoke){a.SessionVersion++;await db.Sessions.Where(x=>x.AccountId==id).ExecuteDeleteAsync(ct);}
            if(!r.IsActive)await db.GroupInvitations.Where(x=>(x.InviterAccountId==id||x.RecipientAccountId==id)&&x.Status==InvitationStatus.Pending)
                .ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Status,InvitationStatus.Canceled),ct);
            Audit(db,clock,Actor(p),id,"User", revoke ? (r.Password!=null ? "PasswordChanged" : r.IsActive ? "AccessUpdated" : "Deactivated") : "Updated");
            try{await db.SaveChangesAsync(ct);}catch(DbUpdateException ex)when(ex.InnerException is PostgresException {SqlState:PostgresErrorCodes.UniqueViolation}){return Error(409,"E-mail já cadastrado.");}
            await tx.CommitAsync(ct);return Results.Ok(User(a));
        });
        api.MapDelete("/users/{id:guid}",async (Guid id,long revision,ClaimsPrincipal p,TimeProvider clock,AuthDb db,CancellationToken ct)=>{
            await using var tx=await db.Database.BeginTransactionAsync(ct);await Lock(db,ct);
            if(!await db.Accounts.AnyAsync(x=>x.Id==Actor(p)&&x.IsActive&&x.IsSystemAdmin&&x.DeletedAt==null,ct))return Results.Forbid();
            var a=await db.Accounts.FromSqlInterpolated($"SELECT * FROM \"Accounts\" WHERE \"Id\"={id} FOR NO KEY UPDATE").SingleOrDefaultAsync(ct);
            if(a is null)return Results.NotFound();if(a.Revision!=revision)return Error(409,"Registro alterado.");
            if(id==Actor(p))return Error(409,"Você não pode excluir a própria conta.");
            if(a.IsSystemAdmin&&a.IsActive&&await db.Accounts.CountAsync(x=>x.IsSystemAdmin&&x.IsActive&&x.DeletedAt==null,ct)<=1)return Error(409,"Mantenha um administrador ativo.");
            if(await db.Groups.AnyAsync(x=>x.OwnerAccountId==id&&x.DeletedAt==null,ct))return Error(409,"Transfira ou exclua os grupos desta conta antes de excluí-la.");
            a.DeletedAt=clock.GetUtcNow();a.IsActive=false;a.SessionVersion++;a.Revision++;
            await db.Sessions.Where(x=>x.AccountId==id).ExecuteDeleteAsync(ct);
            await db.GroupInvitations.Where(x=>(x.InviterAccountId==id||x.RecipientAccountId==id)&&x.Status==InvitationStatus.Pending)
                .ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Status,InvitationStatus.Canceled),ct);
            Audit(db,clock,Actor(p),id,"User","Deleted");await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return Results.NoContent();
        });
        api.MapPost("/users/{id:guid}/restore",async (Guid id,AdminRevision r,ClaimsPrincipal p,TimeProvider clock,AuthDb db,CancellationToken ct)=>{
            await using var tx=await db.Database.BeginTransactionAsync(ct);await Lock(db,ct);
            if(!await db.Accounts.AnyAsync(x=>x.Id==Actor(p)&&x.IsActive&&x.IsSystemAdmin&&x.DeletedAt==null,ct))return Results.Forbid();
            var a=await db.Accounts.SingleOrDefaultAsync(x=>x.Id==id,ct);if(a is null)return Results.NotFound();
            if(a.Revision!=r.Revision)return Error(409,"Registro alterado.");if(a.DeletedAt==null)return Error(409,"Esta conta não está excluída.");a.DeletedAt=null;a.IsActive=false;a.Revision++;
            Audit(db,clock,Actor(p),id,"User","Restored");await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return Results.Ok(User(a));
        });
        api.MapGet("/groups",async(int? page,string? q,string? status,TimeProvider clock,AuthDb db,CancellationToken ct)=>{
            var number=Math.Clamp(page??1,1,100000);var query=db.Groups.AsNoTracking().AsQueryable();
            if(!string.IsNullOrWhiteSpace(q)){var term=q.Trim();if(term.Length>100)return Error(400,"Busca muito longa.");var pattern=Like(term);query=query.Where(x=>EF.Functions.ILike(x.Name,pattern,@"\"));}
            query=status switch {"active"=>query.Where(x=>x.IsActive&&x.DeletedAt==null),"inactive"=>query.Where(x=>!x.IsActive&&x.DeletedAt==null),"deleted"=>query.Where(x=>x.DeletedAt!=null),_=>query};
            var total=await query.LongCountAsync(ct);var rows=await query.OrderBy(x=>x.Name).ThenBy(x=>x.Id).Skip((number-1)*20).Take(20).ToListAsync(ct);
            var ownerIds=rows.Select(x=>x.OwnerAccountId).Distinct().ToArray();
            var ownerEmails=await db.Accounts.Where(x=>ownerIds.Contains(x.Id)).ToDictionaryAsync(x=>x.Id,x=>x.Email,ct);
            return Results.Ok(new AdminPage<AdminGroup>(rows.Select(x=>Group(x) with {OwnerEmail=ownerEmails.GetValueOrDefault(x.OwnerAccountId)}).ToArray(),total,number,20));
        });
        api.MapGet("/groups/{id:guid}",async(Guid id,TimeProvider clock,AuthDb db,CancellationToken ct)=>{
            var g=await db.Groups.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct);if(g is null)return Results.NotFound();
            var members=await(from m in db.GroupMemberships join a in db.Accounts on m.AccountId equals a.Id where m.GroupId==id
                select new {a.Id,a.Name,a.Email,a.IsActive,m.Role}).Take(100).ToListAsync(ct);
            var invites=await db.GroupInvitations.Where(x=>x.GroupId==id).OrderByDescending(x=>x.CreatedAt)
                .Select(x=>new{x.Id,x.RecipientAccountId,x.Status,x.ExpiresAt}).Take(100).ToListAsync(ct);
            var alarms=await db.GroupAlarms.AsNoTracking().Where(x=>x.GroupId==id&&!x.Deleted).OrderBy(x=>x.Id).Take(100).ToListAsync(ct);
            return Results.Ok(new{group=Group(g),members,invitations=invites,alarms=alarms.Select(x=>x.Response(g.Name,false))});
        });
        api.MapPost("/groups",async(AdminGroupRequest r,ClaimsPrincipal p,TimeProvider clock,AuthDb db,CancellationToken ct)=>{
            if(!ValidGroup(r))return Error(400,"Informe nome de até 100 caracteres e descrição de até 500.");
            await using var tx=await db.Database.BeginTransactionAsync(ct);await Lock(db,ct);
            if(!await db.Accounts.AnyAsync(x=>x.Id==Actor(p)&&x.IsActive&&x.IsSystemAdmin&&x.DeletedAt==null,ct))return Results.Forbid();
            var ownerId=r.OwnerAccountId;
            if(!string.IsNullOrWhiteSpace(r.OwnerEmail))ownerId=await db.Accounts.Where(x=>x.NormalizedEmail==r.OwnerEmail.Trim().ToUpperInvariant()).Select(x=>x.Id).SingleOrDefaultAsync(ct);
            var owner=await db.Accounts.FromSqlInterpolated($"SELECT * FROM \"Accounts\" WHERE \"Id\"={ownerId} FOR NO KEY UPDATE").SingleOrDefaultAsync(ct);
            if(owner is null||!owner.IsActive||owner.DeletedAt!=null)return Error(400,"Escolha um proprietário ativo.");
            if(await db.Groups.CountAsync(x=>x.OwnerAccountId==owner.Id&&x.DeletedAt==null,ct)>=20||await db.GroupMemberships.CountAsync(x=>x.AccountId==owner.Id,ct)>=100)
                return Error(422,"Proprietário atingiu o limite de grupos.");
            var g=new TeamGroup{Name=r.Name!.Trim(),Description=r.Description?.Trim()??"",OwnerAccountId=owner.Id,IsActive=r.IsActive,CreatedAt=clock.GetUtcNow()};
            db.Groups.Add(g);db.GroupMemberships.Add(new(){GroupId=g.Id,AccountId=owner.Id,Role=GroupRole.Owner,JoinedAt=g.CreatedAt});
            Audit(db,clock,Actor(p),g.Id,"Group","Created");await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return Results.Created($"/api/admin/groups/{g.Id}",Group(g));
        });
        api.MapPatch("/groups/{id:guid}",async(Guid id,AdminGroupRequest r,ClaimsPrincipal p,TimeProvider clock,AuthDb db,CancellationToken ct)=>{
            if(!ValidGroup(r))return Error(400,"Informe nome de até 100 caracteres e descrição de até 500.");
            await using var tx=await db.Database.BeginTransactionAsync(ct);await Lock(db,ct);
            if(!await db.Accounts.AnyAsync(x=>x.Id==Actor(p)&&x.IsActive&&x.IsSystemAdmin&&x.DeletedAt==null,ct))return Results.Forbid();
            var g=await db.Groups.FromSqlInterpolated($"SELECT * FROM \"Groups\" WHERE \"Id\"={id} FOR UPDATE").SingleOrDefaultAsync(ct);
            if(g is null)return Results.NotFound();if(g.Revision!=r.Revision)return Error(409,"Grupo alterado. Atualize a lista.");
            if(g.DeletedAt!=null)return Error(409,"Restaure o grupo antes de editar.");
            var ownerId=r.OwnerAccountId;
            if(!string.IsNullOrWhiteSpace(r.OwnerEmail))ownerId=await db.Accounts.Where(x=>x.NormalizedEmail==r.OwnerEmail.Trim().ToUpperInvariant()).Select(x=>x.Id).SingleOrDefaultAsync(ct);
            var owner=await db.Accounts.FromSqlInterpolated($"SELECT * FROM \"Accounts\" WHERE \"Id\"={ownerId} FOR NO KEY UPDATE").SingleOrDefaultAsync(ct);
            if(owner is null||owner.DeletedAt!=null||(!owner.IsActive&&(r.IsActive||owner.Id!=g.OwnerAccountId)))return Error(400,"Escolha um proprietário ativo.");
            if(g.OwnerAccountId!=owner.Id){
                if(await db.Groups.CountAsync(x=>x.OwnerAccountId==owner.Id&&x.DeletedAt==null,ct)>=20)return Error(422,"Proprietário atingiu o limite de grupos.");
                var previous=await db.GroupMemberships.SingleAsync(x=>x.GroupId==id&&x.AccountId==g.OwnerAccountId,ct);previous.Role=GroupRole.Administrator;
                var next=await db.GroupMemberships.SingleOrDefaultAsync(x=>x.GroupId==id&&x.AccountId==owner.Id,ct);
                if(next is null){if(await db.GroupMemberships.CountAsync(x=>x.GroupId==id,ct)>=100||await db.GroupMemberships.CountAsync(x=>x.AccountId==owner.Id,ct)>=100)return Error(422,"Limite de membros atingido.");
                    next=new(){GroupId=id,AccountId=owner.Id,JoinedAt=clock.GetUtcNow()};db.GroupMemberships.Add(next);}
                next.Role=GroupRole.Owner;g.OwnerAccountId=owner.Id;
            }
            g.Name=r.Name!.Trim();g.Description=r.Description?.Trim()??"";g.IsActive=r.IsActive;g.Revision++;
            if(!r.IsActive)await db.GroupInvitations.Where(x=>x.GroupId==id&&x.Status==InvitationStatus.Pending).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Status,InvitationStatus.Canceled),ct);
            Audit(db,clock,Actor(p),id,"Group",r.IsActive?"Updated":"Deactivated");await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return Results.Ok(Group(g));
        });
        api.MapDelete("/groups/{id:guid}",async(Guid id,long revision,ClaimsPrincipal p,TimeProvider clock,AuthDb db,CancellationToken ct)=>{
            await using var tx=await db.Database.BeginTransactionAsync(ct);await Lock(db,ct);
            if(!await db.Accounts.AnyAsync(x=>x.Id==Actor(p)&&x.IsActive&&x.IsSystemAdmin&&x.DeletedAt==null,ct))return Results.Forbid();
            var g=await db.Groups.FromSqlInterpolated($"SELECT * FROM \"Groups\" WHERE \"Id\"={id} FOR UPDATE").SingleOrDefaultAsync(ct);
            if(g is null)return Results.NotFound();if(g.Revision!=revision)return Error(409,"Grupo alterado.");
            g.DeletedAt=clock.GetUtcNow();g.IsActive=false;g.Revision++;
            await db.GroupInvitations.Where(x=>x.GroupId==id&&x.Status==InvitationStatus.Pending).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Status,InvitationStatus.Canceled),ct);
            Audit(db,clock,Actor(p),id,"Group","Deleted");await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return Results.NoContent();
        });
        api.MapPost("/groups/{id:guid}/restore",async(Guid id,AdminRevision r,ClaimsPrincipal p,TimeProvider clock,AuthDb db,CancellationToken ct)=>{
            await using var tx=await db.Database.BeginTransactionAsync(ct);await Lock(db,ct);
            if(!await db.Accounts.AnyAsync(x=>x.Id==Actor(p)&&x.IsActive&&x.IsSystemAdmin&&x.DeletedAt==null,ct))return Results.Forbid();
            var g=await db.Groups.FromSqlInterpolated($"SELECT * FROM \"Groups\" WHERE \"Id\"={id} FOR UPDATE").SingleOrDefaultAsync(ct);
            if(g is null)return Results.NotFound();if(g.Revision!=r.Revision)return Error(409,"Grupo alterado.");
            if(g.DeletedAt==null)return Error(409,"Este grupo não está excluído.");
            g.DeletedAt=null;g.IsActive=false;g.Revision++;Audit(db,clock,Actor(p),id,"Group","Restored");
            await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return Results.Ok(Group(g));
        });
        api.MapGet("/audit",async(int? page,TimeProvider clock,AuthDb db,CancellationToken ct)=>{
            var number=Math.Clamp(page??1,1,100000);var total=await db.AdminAuditEvents.LongCountAsync(ct);
            // Names make the log readable; identifiers stay for records that were renamed or removed.
            var rows=await db.AdminAuditEvents.AsNoTracking().OrderByDescending(x=>x.CreatedAt).ThenBy(x=>x.Id).Skip((number-1)*20).Take(20)
                .Select(x=>new AdminAuditItem(x.Id,x.ActorAccountId,x.TargetId,x.Entity,x.Action,x.CreatedAt,
                    db.Accounts.Where(a=>a.Id==x.ActorAccountId).Select(a=>a.Name).FirstOrDefault(),
                    x.Entity=="User"?db.Accounts.Where(a=>a.Id==x.TargetId).Select(a=>a.Name).FirstOrDefault():db.Groups.Where(g=>g.Id==x.TargetId).Select(g=>g.Name).FirstOrDefault()))
                .ToListAsync(ct);
            return Results.Ok(new AdminPage<AdminAuditItem>(rows,total,number,20));
        });
    }
}
public static class AdminBootstrap
{
    public static async Task RunAsync(IServiceProvider services)
    {
        using var scope=services.CreateScope();
        var config=scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var db=scope.ServiceProvider.GetRequiredService<AuthDb>();
        var hasher=scope.ServiceProvider.GetRequiredService<IPasswordHasher<Account>>();
        var clock=scope.ServiceProvider.GetRequiredService<TimeProvider>();
        var email=config["Admin:Email"];var password=config["Admin:Password"];
        var errors=RegistrationValidation.Validate(new(config["Admin:Name"]??"Administrador",email,password));
        if(errors.Count>0)throw new InvalidOperationException("Configure Admin__Email, Admin__Password (6 a 128 caracteres) e opcionalmente Admin__Name.");
        await using var tx=await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(734620261015)");
        if(await db.Accounts.AnyAsync(x=>x.IsSystemAdmin&&x.IsActive&&x.DeletedAt==null))throw new InvalidOperationException("Já existe administrador ativo. Use o painel.");
        var normalized=email!.Trim().ToUpperInvariant();
        var a=await db.Accounts.SingleOrDefaultAsync(x=>x.NormalizedEmail==normalized);
        if(a is not null)throw new InvalidOperationException("O e-mail já pertence a uma conta. Escolha um e-mail exclusivo para provisionamento.");
        a=new Account{Name=config["Admin:Name"]??"Administrador",Email=email.Trim(),NormalizedEmail=normalized,IsSystemAdmin=true,CreatedAt=clock.GetUtcNow()};
        a.PasswordHash=hasher.HashPassword(a,password!);db.Accounts.Add(a);
        db.AdminAuditEvents.Add(new(){ActorAccountId=a.Id,TargetId=a.Id,Entity="User",Action="Bootstrap",CreatedAt=clock.GetUtcNow()});
        await db.SaveChangesAsync();await tx.CommitAsync();
        Console.WriteLine("Conta administradora provisionada. Nenhuma senha foi registrada nos logs.");
    }
}
