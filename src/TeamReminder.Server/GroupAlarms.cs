using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
namespace TeamReminder.Server;

public sealed class GroupAlarm
{
    public Guid Id { get; set; }
    public Guid GroupId { get; set; }
    public Guid CreatedByAccountId { get; set; }
    public long Version { get; set; }
    public Guid MutationId { get; set; }
    public bool Deleted { get; set; }
    public string DefinitionJson { get; set; } = "";
    public GroupAlarmResponse Response(string groupName, bool canManage) =>
        new(Id,GroupId,groupName,Version,canManage,JsonSerializer.Deserialize<AlarmDefinition>(DefinitionJson)!);
}
public sealed record GroupAlarmResponse(Guid Id, Guid GroupId, string GroupName, long Version, bool CanManage, AlarmDefinition Alarm);
public static class GroupAlarmEndpoints
{
    public static void MapGroupAlarms(this WebApplication app)
    {
        app.MapGet("/api/group-alarms", async(ClaimsPrincipal principal,AuthDb db,CancellationToken ct)=>{
            var actor=Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var rows=await(from alarm in db.GroupAlarms.AsNoTracking()
                join team in db.Groups on alarm.GroupId equals team.Id
                join member in db.GroupMemberships on team.Id equals member.GroupId
                where member.AccountId==actor && team.DeletedAt==null && team.IsActive && !alarm.Deleted
                orderby alarm.GroupId,alarm.Id
                select new {Alarm=alarm,team.Name,member.Role}).ToListAsync(ct);
            return Results.Ok(rows.Select(x=>x.Alarm.Response(x.Name,x.Role is GroupRole.Owner or GroupRole.Administrator)));
        }).RequireAuthorization().RequireRateLimiting("groups");
        var alarms=app.MapGroup("/api/groups/{groupId:guid}/alarms").RequireAuthorization().RequireRateLimiting("groups");
        alarms.MapPut("/{id:guid}",WriteAsync);
        alarms.MapDelete("/{id:guid}",(Guid groupId,Guid id,long version,Guid mutationId,ClaimsPrincipal principal,AuthDb db,TimeProvider clock,CancellationToken ct)=>
            WriteAsync(groupId,id,new AlarmWriteRequest(version,mutationId,true,null),principal,db,clock,ct));
    }
    private static async Task<IResult> WriteAsync(Guid groupId,Guid id,AlarmWriteRequest request,ClaimsPrincipal principal,AuthDb db,TimeProvider clock,CancellationToken ct)
    {
        var errors=PersonalAlarmEndpoints.Validate(id,request);if(errors.Count>0)return Results.ValidationProblem(errors);
        var actor=Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        var team=await db.Groups.FromSqlInterpolated($"SELECT * FROM \"Groups\" WHERE \"Id\"={groupId} FOR UPDATE").SingleOrDefaultAsync(ct);
        if(team is null||team.DeletedAt!=null||!team.IsActive)return Results.NotFound();
        var account=await db.Accounts.FromSqlInterpolated($"SELECT * FROM \"Accounts\" WHERE \"Id\"={actor} FOR NO KEY UPDATE").SingleAsync(ct);
        if(!account.IsActive||account.DeletedAt!=null)return Results.Unauthorized();
        var member=await db.GroupMemberships.SingleOrDefaultAsync(x=>x.GroupId==groupId&&x.AccountId==actor,ct);
        if(member is null)return Results.NotFound();
        if(member.Role is not(GroupRole.Owner or GroupRole.Administrator))return Results.Forbid();
        var alarm=await db.GroupAlarms.SingleOrDefaultAsync(x=>x.Id==id,ct);
        if(alarm is not null&&alarm.GroupId!=groupId)return Results.Conflict(new{message="Identificador já utilizado."});
        if(alarm is not null&&alarm.MutationId==request.MutationId)
            return alarm.Deleted?Results.NoContent():Results.Ok(alarm.Response(team.Name,true));
        if((alarm?.Version??0)!=request.ExpectedVersion)return Results.Conflict(new{message="O alarme do grupo foi alterado. Sincronize e tente novamente."});
        if(alarm is null){
            if(request.Deleted)return Results.NotFound();
            if(await db.GroupAlarms.CountAsync(x=>x.GroupId==groupId,ct)>=10_000)return Results.Json(new{message="Limite de registros do grupo atingido, incluindo exclusões."},statusCode:422);
            if(await db.GroupAlarms.CountAsync(x=>x.GroupId==groupId&&!x.Deleted,ct)>=100)return Results.Json(new{message="Limite de 100 alarmes ativos por grupo atingido."},statusCode:422);
            if(await db.PersonalAlarms.AnyAsync(x=>x.Id==id,ct))return Results.Conflict(new{message="Identificador já utilizado."});
            alarm=new(){Id=id,GroupId=groupId,CreatedByAccountId=actor};db.GroupAlarms.Add(alarm);
        }
        if(alarm.Deleted)return Results.Conflict(new{message="Este alarme foi excluído. Crie um novo alarme."});
        if(!request.Deleted){
            var definition=request.Alarm!;
            definition=definition with {
                Title=definition.Title.Trim(),
                ScheduledAtUtc=DateTimeOffset.FromUnixTimeMilliseconds(definition.ScheduledAtUtc.ToUnixTimeMilliseconds()),
                CreatedAtUtc=alarm.Version==0?DateTimeOffset.FromUnixTimeMilliseconds(definition.CreatedAtUtc.ToUnixTimeMilliseconds()):JsonSerializer.Deserialize<AlarmDefinition>(alarm.DefinitionJson)!.CreatedAtUtc,
                LinkUrl=string.IsNullOrWhiteSpace(definition.LinkUrl)?null:new Uri(definition.LinkUrl.Trim()).AbsoluteUri,
                LinkLabel=string.IsNullOrWhiteSpace(definition.LinkUrl)?null:string.IsNullOrWhiteSpace(definition.LinkLabel)?"Abrir link":definition.LinkLabel.Trim()
            };
            if(definition.Recurrence==0&&definition.ScheduledAtUtc<=clock.GetUtcNow())return Results.ValidationProblem(new Dictionary<string,string[]>{{"schedule",["Escolha uma data futura para o alarme do grupo."]}});
            alarm.DefinitionJson=JsonSerializer.Serialize(definition);
        }
        alarm.Version++;alarm.MutationId=request.MutationId;alarm.Deleted=request.Deleted;
        db.GroupAuditEvents.Add(new(){GroupId=groupId,ActorAccountId=actor,Action=request.Deleted?"AlarmDeleted":alarm.Version==1?"AlarmCreated":"AlarmUpdated",CreatedAt=clock.GetUtcNow()});
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
        return alarm.Deleted?Results.NoContent():Results.Ok(alarm.Response(team.Name,true));
    }
}
