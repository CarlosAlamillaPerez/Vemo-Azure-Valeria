using Microsoft.AspNetCore.Authentication;
using VemoLab.Api;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<CosmosStore>();
builder.Services.AddSingleton<SharePointSync>();

// Quien eres: Easy Auth (Azure) o Auth:DevUser (local). Que puedes: usuarios_roles. Ver EasyAuthHandler.
builder.Services.AddAuthentication(EasyAuthHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, EasyAuthHandler>(EasyAuthHandler.SchemeName, _ => { });
builder.Services.AddAuthorization();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Siembra el primer administrador si existe Auth:BootstrapAdmin (idempotente).
// Tambien crea el contenedor usuarios_roles la primera vez.
var bootstrapAdmin = app.Configuration["Auth:BootstrapAdmin"];
if (!string.IsNullOrWhiteSpace(bootstrapAdmin))
{
    try
    {
        await app.Services.GetRequiredService<CosmosStore>()
            .EnsureBootstrapAdminAsync(bootstrapAdmin, CancellationToken.None);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "No se pudo sembrar el administrador inicial");
    }
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.UseDefaultFiles();

app.UseStaticFiles();

app.MapControllers();

app.Run();
