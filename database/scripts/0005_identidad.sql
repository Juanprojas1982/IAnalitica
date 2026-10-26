/* 0005 · Identity + OpenIddict en el esquema seg. GENERADO por database/generar-identidad.sh: no editar a mano. */
BEGIN TRANSACTION;
IF SCHEMA_ID(N'seg') IS NULL EXEC(N'CREATE SCHEMA [seg];');

CREATE TABLE [seg].[OidcAlcance] (
    [Id] nvarchar(450) NOT NULL,
    [ConcurrencyToken] nvarchar(50) NULL,
    [Description] nvarchar(max) NULL,
    [Descriptions] nvarchar(max) NULL,
    [DisplayName] nvarchar(max) NULL,
    [DisplayNames] nvarchar(max) NULL,
    [Name] nvarchar(200) NULL,
    [Properties] nvarchar(max) NULL,
    [Resources] nvarchar(max) NULL,
    CONSTRAINT [PK_OidcAlcance] PRIMARY KEY ([Id])
);

CREATE TABLE [seg].[OidcAplicacion] (
    [Id] nvarchar(450) NOT NULL,
    [ApplicationType] nvarchar(50) NULL,
    [ClientId] nvarchar(100) NULL,
    [ClientSecret] nvarchar(max) NULL,
    [ClientType] nvarchar(50) NULL,
    [ConcurrencyToken] nvarchar(50) NULL,
    [ConsentType] nvarchar(50) NULL,
    [DisplayName] nvarchar(max) NULL,
    [DisplayNames] nvarchar(max) NULL,
    [JsonWebKeySet] nvarchar(max) NULL,
    [Permissions] nvarchar(max) NULL,
    [PostLogoutRedirectUris] nvarchar(max) NULL,
    [Properties] nvarchar(max) NULL,
    [RedirectUris] nvarchar(max) NULL,
    [Requirements] nvarchar(max) NULL,
    [Settings] nvarchar(max) NULL,
    CONSTRAINT [PK_OidcAplicacion] PRIMARY KEY ([Id])
);

CREATE TABLE [seg].[Rol] (
    [Id] int NOT NULL IDENTITY,
    [Name] nvarchar(256) NULL,
    [NormalizedName] nvarchar(256) NULL,
    [ConcurrencyStamp] nvarchar(max) NULL,
    CONSTRAINT [PK_Rol] PRIMARY KEY ([Id])
);

CREATE TABLE [seg].[Usuario] (
    [Id] int NOT NULL IDENTITY,
    [EmpresaId] int NULL,
    [EsSuperAdmin] bit NOT NULL DEFAULT CAST(0 AS bit),
    [NombreCompleto] nvarchar(150) NOT NULL DEFAULT N'',
    [DebeCambiarClave] bit NOT NULL DEFAULT CAST(0 AS bit),
    [ClaveTemporalVenceEn] datetime2 NULL,
    [EsAdminPrincipal] bit NOT NULL DEFAULT CAST(0 AS bit),
    [Activo] bit NOT NULL DEFAULT CAST(1 AS bit),
    [CreadoEn] datetime2 NOT NULL DEFAULT (SYSUTCDATETIME()),
    [CreadoPor] int NULL,
    [UserName] nvarchar(256) NULL,
    [NormalizedUserName] nvarchar(256) NULL,
    [Email] nvarchar(256) NULL,
    [NormalizedEmail] nvarchar(256) NULL,
    [EmailConfirmed] bit NOT NULL,
    [PasswordHash] nvarchar(max) NULL,
    [SecurityStamp] nvarchar(max) NULL,
    [ConcurrencyStamp] nvarchar(max) NULL,
    [PhoneNumber] nvarchar(max) NULL,
    [PhoneNumberConfirmed] bit NOT NULL,
    [TwoFactorEnabled] bit NOT NULL,
    [LockoutEnd] datetimeoffset NULL,
    [LockoutEnabled] bit NOT NULL,
    [AccessFailedCount] int NOT NULL,
    CONSTRAINT [PK_Usuario] PRIMARY KEY ([Id])
);

CREATE TABLE [seg].[OidcAutorizacion] (
    [Id] nvarchar(450) NOT NULL,
    [ApplicationId] nvarchar(450) NULL,
    [ConcurrencyToken] nvarchar(50) NULL,
    [CreationDate] datetime2 NULL,
    [Properties] nvarchar(max) NULL,
    [Scopes] nvarchar(max) NULL,
    [Status] nvarchar(50) NULL,
    [Subject] nvarchar(400) NULL,
    [Type] nvarchar(50) NULL,
    CONSTRAINT [PK_OidcAutorizacion] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_OidcAutorizacion_OidcAplicacion_ApplicationId] FOREIGN KEY ([ApplicationId]) REFERENCES [seg].[OidcAplicacion] ([Id])
);

CREATE TABLE [seg].[RolClaim] (
    [Id] int NOT NULL IDENTITY,
    [RoleId] int NOT NULL,
    [ClaimType] nvarchar(max) NULL,
    [ClaimValue] nvarchar(max) NULL,
    CONSTRAINT [PK_RolClaim] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_RolClaim_Rol_RoleId] FOREIGN KEY ([RoleId]) REFERENCES [seg].[Rol] ([Id]) ON DELETE CASCADE
);

CREATE TABLE [seg].[UsuarioClaim] (
    [Id] int NOT NULL IDENTITY,
    [UserId] int NOT NULL,
    [ClaimType] nvarchar(max) NULL,
    [ClaimValue] nvarchar(max) NULL,
    CONSTRAINT [PK_UsuarioClaim] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_UsuarioClaim_Usuario_UserId] FOREIGN KEY ([UserId]) REFERENCES [seg].[Usuario] ([Id]) ON DELETE CASCADE
);

CREATE TABLE [seg].[UsuarioLogin] (
    [LoginProvider] nvarchar(450) NOT NULL,
    [ProviderKey] nvarchar(450) NOT NULL,
    [ProviderDisplayName] nvarchar(max) NULL,
    [UserId] int NOT NULL,
    CONSTRAINT [PK_UsuarioLogin] PRIMARY KEY ([LoginProvider], [ProviderKey]),
    CONSTRAINT [FK_UsuarioLogin_Usuario_UserId] FOREIGN KEY ([UserId]) REFERENCES [seg].[Usuario] ([Id]) ON DELETE CASCADE
);

CREATE TABLE [seg].[UsuarioRol] (
    [UserId] int NOT NULL,
    [RoleId] int NOT NULL,
    CONSTRAINT [PK_UsuarioRol] PRIMARY KEY ([UserId], [RoleId]),
    CONSTRAINT [FK_UsuarioRol_Rol_RoleId] FOREIGN KEY ([RoleId]) REFERENCES [seg].[Rol] ([Id]) ON DELETE CASCADE,
    CONSTRAINT [FK_UsuarioRol_Usuario_UserId] FOREIGN KEY ([UserId]) REFERENCES [seg].[Usuario] ([Id]) ON DELETE CASCADE
);

CREATE TABLE [seg].[UsuarioToken] (
    [UserId] int NOT NULL,
    [LoginProvider] nvarchar(450) NOT NULL,
    [Name] nvarchar(450) NOT NULL,
    [Value] nvarchar(max) NULL,
    CONSTRAINT [PK_UsuarioToken] PRIMARY KEY ([UserId], [LoginProvider], [Name]),
    CONSTRAINT [FK_UsuarioToken_Usuario_UserId] FOREIGN KEY ([UserId]) REFERENCES [seg].[Usuario] ([Id]) ON DELETE CASCADE
);

CREATE TABLE [seg].[OidcToken] (
    [Id] nvarchar(450) NOT NULL,
    [ApplicationId] nvarchar(450) NULL,
    [AuthorizationId] nvarchar(450) NULL,
    [ConcurrencyToken] nvarchar(50) NULL,
    [CreationDate] datetime2 NULL,
    [ExpirationDate] datetime2 NULL,
    [Payload] nvarchar(max) NULL,
    [Properties] nvarchar(max) NULL,
    [RedemptionDate] datetime2 NULL,
    [ReferenceId] nvarchar(100) NULL,
    [Status] nvarchar(50) NULL,
    [Subject] nvarchar(400) NULL,
    [Type] nvarchar(150) NULL,
    CONSTRAINT [PK_OidcToken] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_OidcToken_OidcAplicacion_ApplicationId] FOREIGN KEY ([ApplicationId]) REFERENCES [seg].[OidcAplicacion] ([Id]),
    CONSTRAINT [FK_OidcToken_OidcAutorizacion_AuthorizationId] FOREIGN KEY ([AuthorizationId]) REFERENCES [seg].[OidcAutorizacion] ([Id])
);

CREATE UNIQUE INDEX [IX_OidcAlcance_Name] ON [seg].[OidcAlcance] ([Name]) WHERE [Name] IS NOT NULL;

CREATE UNIQUE INDEX [IX_OidcAplicacion_ClientId] ON [seg].[OidcAplicacion] ([ClientId]) WHERE [ClientId] IS NOT NULL;

CREATE INDEX [IX_OidcAutorizacion_ApplicationId_Status_Subject_Type] ON [seg].[OidcAutorizacion] ([ApplicationId], [Status], [Subject], [Type]);

CREATE INDEX [IX_OidcToken_ApplicationId_Status_Subject_Type] ON [seg].[OidcToken] ([ApplicationId], [Status], [Subject], [Type]);

CREATE INDEX [IX_OidcToken_AuthorizationId] ON [seg].[OidcToken] ([AuthorizationId]);

CREATE UNIQUE INDEX [IX_OidcToken_ReferenceId] ON [seg].[OidcToken] ([ReferenceId]) WHERE [ReferenceId] IS NOT NULL;

CREATE UNIQUE INDEX [RoleNameIndex] ON [seg].[Rol] ([NormalizedName]) WHERE [NormalizedName] IS NOT NULL;

CREATE INDEX [IX_RolClaim_RoleId] ON [seg].[RolClaim] ([RoleId]);

CREATE INDEX [EmailIndex] ON [seg].[Usuario] ([NormalizedEmail]);

CREATE UNIQUE INDEX [UserNameIndex] ON [seg].[Usuario] ([NormalizedUserName]) WHERE [NormalizedUserName] IS NOT NULL;

CREATE INDEX [IX_UsuarioClaim_UserId] ON [seg].[UsuarioClaim] ([UserId]);

CREATE INDEX [IX_UsuarioLogin_UserId] ON [seg].[UsuarioLogin] ([UserId]);

CREATE INDEX [IX_UsuarioRol_RoleId] ON [seg].[UsuarioRol] ([RoleId]);

COMMIT;
GO

