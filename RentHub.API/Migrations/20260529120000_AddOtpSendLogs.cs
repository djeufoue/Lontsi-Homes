using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RentHub.API.Data;

#nullable disable

namespace RentHub.API.Migrations
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260529120000_AddOtpSendLogs")]
    public partial class AddOtpSendLogs : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF OBJECT_ID(N'[OtpSendLogs]', N'U') IS NULL
BEGIN
    CREATE TABLE [OtpSendLogs]
    (
        [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_OtpSendLogs] PRIMARY KEY,
        [UserId] nvarchar(450) NOT NULL,
        [Purpose] nvarchar(64) NOT NULL,
        [Channel] nvarchar(20) NOT NULL,
        [Recipient] nvarchar(64) NOT NULL,
        [SentAt] datetimeoffset NOT NULL
            CONSTRAINT [DF_OtpSendLogs_SentAt] DEFAULT(SYSDATETIMEOFFSET()),
        CONSTRAINT [FK_OtpSendLogs_AspNetUsers_UserId]
            FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers]([Id]) ON DELETE CASCADE
    );
END

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_OtpSendLogs_UserId_Purpose_SentAt'
      AND [object_id] = OBJECT_ID(N'[OtpSendLogs]')
)
BEGIN
    CREATE INDEX [IX_OtpSendLogs_UserId_Purpose_SentAt]
    ON [OtpSendLogs]([UserId], [Purpose], [SentAt]);
END

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_OtpSendLogs_UserId_Purpose_Recipient_SentAt'
      AND [object_id] = OBJECT_ID(N'[OtpSendLogs]')
)
BEGIN
    CREATE INDEX [IX_OtpSendLogs_UserId_Purpose_Recipient_SentAt]
    ON [OtpSendLogs]([UserId], [Purpose], [Recipient], [SentAt]);
END
");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF OBJECT_ID(N'[OtpSendLogs]', N'U') IS NOT NULL
BEGIN
    DROP TABLE [OtpSendLogs];
END
");
        }
    }
}
