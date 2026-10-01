using System.Threading.RateLimiting;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using AgenticUrlShortener.Api.Domain;
using AgenticUrlShortener.Api.Infrastructure;
using AgenticUrlShortener.Api.Services;
using AgenticUrlShortener.Api.Workflows;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
var authAuthority = builder.Configuration["Authentication:Authority"];
var authAudience = builder.Configuration["Authentication:Audience"];
var authenticationConfigured = !string.IsNullOrWhiteSpace(authAuthority) && !string.IsNullOrWhiteSpace(authAudience);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
	options.MapInboundClaims = false;
	options.RequireHttpsMetadata = true;
	options.TokenValidationParameters.NameClaimType = "name";
	options.TokenValidationParameters.RoleClaimType = "roles";
	if (authenticationConfigured)
	{
		options.Authority = authAuthority;
		options.Audience = authAudience;
	}
	else
	{
		options.Events = new JwtBearerEvents
		{
			OnMessageReceived = context =>
			{
				context.NoResult();
				return Task.CompletedTask;
			}
		};
	}
});
builder.Services.AddAuthorization(options =>
{
	options.AddPolicy("workflow.read", policy => policy.RequireAuthenticatedUser()
		.RequireAssertion(context => WorkflowAuthorization.HasPermission(context.User, "workflow.read")));
	options.AddPolicy("workflow.execute", policy => policy.RequireAuthenticatedUser()
		.RequireAssertion(context => WorkflowAuthorization.HasPermission(context.User, "workflow.execute")));
	options.AddPolicy("workflow.approve", policy => policy.RequireAuthenticatedUser()
		.RequireAssertion(context => WorkflowAuthorization.HasPermission(context.User, "workflow.approve")));
	options.AddPolicy("workflow.operator", policy => policy.RequireAuthenticatedUser()
		.RequireAssertion(context => WorkflowAuthorization.HasPermission(context.User, "workflow.operator")));
});
var dataPath = builder.Configuration["Data:Path"] ?? Path.Combine(builder.Environment.ContentRootPath, "App_Data", "links.json");
builder.Services.AddSingleton<IShortLinkRepository>(_ => new JsonShortLinkRepository(dataPath));
builder.Services.AddSingleton<ShortLinkService>();
if (builder.Configuration.GetValue<bool>("AI:AllowExternalProcessing"))
{
	var endpointValue = builder.Configuration["AI:ChatCompletionsUrl"];
	if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out var modelEndpoint) ||
		(modelEndpoint.Scheme != Uri.UriSchemeHttps && !modelEndpoint.IsLoopback))
		throw new InvalidOperationException("AI:ChatCompletionsUrl must use HTTPS, except for a loopback development endpoint.");
	if (string.IsNullOrWhiteSpace(builder.Configuration["AI:Model"]))
		throw new InvalidOperationException("AI:Model is required when external AI processing is enabled.");
	builder.Services.AddHttpClient<IStageAgent, OpenAiCompatibleStageAgent>(client => client.Timeout = TimeSpan.FromSeconds(45));
}
else
{
	builder.Services.AddSingleton<IStageAgent, RuleBasedStageAgent>();
}
builder.Services.AddSingleton<WorkflowOrchestrator>();
builder.Services.AddRateLimiter(options => options.AddFixedWindowLimiter("writes", limiter =>
{
	limiter.PermitLimit = 30;
	limiter.Window = TimeSpan.FromMinutes(1);
	limiter.QueueLimit = 0;
}));

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.MapPost("/api/links", async (CreateLinkRequest request, ShortLinkService service, HttpContext context, CancellationToken cancellationToken) =>
{
	try
	{
		var link = await service.CreateAsync(request, cancellationToken);
		var shortUrl = $"{context.Request.Scheme}://{context.Request.Host}/r/{link.Code}";
		return Results.Created($"/api/links/{link.Code}", new CreateLinkResponse(link.Code, shortUrl, link.Destination, link.CreatedAt, link.ExpiresAt));
	}
	catch (ArgumentException exception)
	{
		return Results.BadRequest(new { error = exception.Message });
	}
}).RequireRateLimiting("writes");

app.MapGet("/r/{code}", async (string code, ShortLinkService service, CancellationToken cancellationToken) =>
{
	var destination = await service.ResolveAsync(code, cancellationToken);
	return destination is null ? Results.NotFound() : Results.Redirect(destination);
});
app.MapGet("/api/links/{code}/analytics", async (string code, ShortLinkService service, CancellationToken cancellationToken) =>
{
	var analytics = await service.GetAnalyticsAsync(code, cancellationToken);
	return analytics is null ? Results.NotFound() : Results.Ok(analytics);
});

app.MapPost("/api/workflows", async (StartWorkflowRequest request, WorkflowOrchestrator orchestrator, HttpContext context, CancellationToken cancellationToken) =>
{
	try
	{
		var run = await orchestrator.StartAsync(request, WorkflowAuthorization.GetActor(context.User), cancellationToken);
		return Results.Created($"/api/workflows/{run.Id}", run);
	}
	catch (ArgumentException exception)
	{
		return Results.BadRequest(new { error = exception.Message });
	}
}).RequireAuthorization("workflow.execute").RequireRateLimiting("writes");
app.MapGet("/api/workflows", async (WorkflowOrchestrator orchestrator) => Results.Ok(await orchestrator.ListAsync()))
	.RequireAuthorization("workflow.read");
app.MapGet("/api/workflows/metrics", async (WorkflowOrchestrator orchestrator) => Results.Ok(await orchestrator.GetMetricsAsync()))
	.RequireAuthorization("workflow.read");
app.MapGet("/api/workflows/{id}", async (string id, WorkflowOrchestrator orchestrator) =>
	await orchestrator.GetAsync(id) is { } run ? Results.Ok(run) : Results.NotFound())
	.RequireAuthorization("workflow.read");

app.MapPost("/api/workflows/{id}/approve", async (string id, ApprovalRequest request, WorkflowOrchestrator orchestrator, HttpContext context, CancellationToken cancellationToken) =>
{
	try { return Results.Ok(await orchestrator.ApproveAsync(id, request, WorkflowAuthorization.GetActor(context.User), cancellationToken)); }
	catch (KeyNotFoundException) { return Results.NotFound(); }
	catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
	catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
}).RequireAuthorization("workflow.approve");
app.MapPost("/api/workflows/{id}/replan", async (string id, ReplanRequest request, WorkflowOrchestrator orchestrator, HttpContext context, CancellationToken cancellationToken) =>
{
	try { return Results.Ok(await orchestrator.ReplanAsync(id, request, WorkflowAuthorization.GetActor(context.User), cancellationToken)); }
	catch (KeyNotFoundException) { return Results.NotFound(); }
	catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
	catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
}).RequireAuthorization("workflow.execute");
app.MapPost("/api/workflows/{id}/safe-stop", async (string id, WorkflowOrchestrator orchestrator, HttpContext context) =>
{
	try { return Results.Ok(await orchestrator.SafeStopAsync(id, WorkflowAuthorization.GetActor(context.User))); }
	catch (KeyNotFoundException) { return Results.NotFound(); }
}).RequireAuthorization("workflow.operator");
app.MapPost("/api/workflows/{id}/resume", async (string id, WorkflowOrchestrator orchestrator, HttpContext context, CancellationToken cancellationToken) =>
{
	try { return Results.Ok(await orchestrator.ResumeAsync(id, WorkflowAuthorization.GetActor(context.User), cancellationToken)); }
	catch (KeyNotFoundException) { return Results.NotFound(); }
	catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
}).RequireAuthorization("workflow.operator");
app.MapPost("/api/workflows/{id}/rollback", async (string id, RollbackRequest request, WorkflowOrchestrator orchestrator, HttpContext context) =>
{
	try { return Results.Ok(await orchestrator.RollbackAsync(id, request, WorkflowAuthorization.GetActor(context.User))); }
	catch (KeyNotFoundException) { return Results.NotFound(); }
	catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
	catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
}).RequireAuthorization("workflow.operator");

app.Run();

public partial class Program;
