using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentHub.API.Migrations
{
    public partial class AddApartmentFloorAndEmailSessionInvalidation : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                IF COL_LENGTH('dbo.AspNetUsers', 'SessionInvalidatedAt') IS NULL
                    ALTER TABLE [AspNetUsers] ADD [SessionInvalidatedAt] datetimeoffset NULL;

                IF COL_LENGTH('dbo.Apartments', 'FloorNumber') IS NULL
                    ALTER TABLE [Apartments] ADD [FloorNumber] int NULL;
                ");

            // Run in a separate command so SQL Server sees FloorNumber while
            // compiling the statements that read and constrain it.
            migrationBuilder.Sql(@"

                UPDATE [Apartments]
                SET [FloorNumber] = CASE
                    WHEN [FloorNumber] IS NULL OR [FloorNumber] < 0 THEN 0
                    WHEN [FloorNumber] > 30 THEN 30
                    ELSE [FloorNumber]
                END
                WHERE [FloorNumber] IS NULL OR [FloorNumber] < 0 OR [FloorNumber] > 30;

                IF EXISTS
                (
                    SELECT 1 FROM sys.columns
                    WHERE [object_id] = OBJECT_ID(N'[dbo].[Apartments]')
                      AND [name] = N'FloorNumber'
                      AND [is_nullable] = 1
                )
                    ALTER TABLE [Apartments] ALTER COLUMN [FloorNumber] int NOT NULL;

                IF NOT EXISTS
                (
                    SELECT 1
                    FROM sys.default_constraints [dc]
                    INNER JOIN sys.columns [column]
                        ON [column].[object_id] = [dc].[parent_object_id]
                       AND [column].[column_id] = [dc].[parent_column_id]
                    WHERE [dc].[parent_object_id] = OBJECT_ID(N'[dbo].[Apartments]')
                      AND [column].[name] = N'FloorNumber'
                )
                    ALTER TABLE [Apartments] ADD CONSTRAINT [DF_Apartments_FloorNumber] DEFAULT(0) FOR [FloorNumber];

                IF NOT EXISTS
                (
                    SELECT 1 FROM sys.check_constraints
                    WHERE [name] = N'CK_Apartments_FloorNumber'
                      AND [parent_object_id] = OBJECT_ID(N'[dbo].[Apartments]')
                )
                    ALTER TABLE [Apartments] WITH CHECK ADD CONSTRAINT [CK_Apartments_FloorNumber]
                    CHECK ([FloorNumber] >= 0 AND [FloorNumber] <= 30);

                UPDATE [PropertyManagerAssignments]
                SET [PermissionFlags] = [PermissionFlags] | 8192,
                    [Permission] = 1
                WHERE [IsDeleted] = 0
                  AND [PermissionFlags] = 744396316838953;");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                IF EXISTS
                (
                    SELECT 1 FROM sys.check_constraints
                    WHERE [name] = N'CK_Apartments_FloorNumber'
                      AND [parent_object_id] = OBJECT_ID(N'[dbo].[Apartments]')
                )
                    ALTER TABLE [Apartments] DROP CONSTRAINT [CK_Apartments_FloorNumber];

                DECLARE @floorDefault sysname;
                SELECT @floorDefault = [dc].[name]
                FROM sys.default_constraints [dc]
                INNER JOIN sys.columns [column]
                    ON [column].[object_id] = [dc].[parent_object_id]
                   AND [column].[column_id] = [dc].[parent_column_id]
                WHERE [dc].[parent_object_id] = OBJECT_ID(N'[dbo].[Apartments]')
                  AND [column].[name] = N'FloorNumber';
                IF @floorDefault IS NOT NULL
                    EXEC(N'ALTER TABLE [Apartments] DROP CONSTRAINT [' + @floorDefault + N']');

                IF COL_LENGTH('dbo.Apartments', 'FloorNumber') IS NOT NULL
                    ALTER TABLE [Apartments] ALTER COLUMN [FloorNumber] int NULL;

                IF COL_LENGTH('dbo.AspNetUsers', 'SessionInvalidatedAt') IS NOT NULL
                    ALTER TABLE [AspNetUsers] DROP COLUMN [SessionInvalidatedAt];
                ");
        }
    }
}
