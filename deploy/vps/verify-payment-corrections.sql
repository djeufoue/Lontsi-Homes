SET NOCOUNT ON;
IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory
    WHERE MigrationId = N'20260914214102_AddManualPaymentCorrections')
    THROW 51005, 'Manual payment correction migration is missing from EF history.', 1;

IF EXISTS (
    SELECT 1
    FROM (VALUES
        (N'CorrectionJson', N'nvarchar', -1),
        (N'CorrectionRequestId', N'nvarchar', 72),
        (N'ReplacementPaymentId', N'int', 4),
        (N'CorrectionTenantNotifiedAt', N'datetimeoffset', 10),
        (N'CorrectionLandlordNotifiedAt', N'datetimeoffset', 10)
    ) expected(name, type_name, max_length)
    LEFT JOIN sys.columns actual
        ON actual.object_id = OBJECT_ID(N'dbo.Payments') AND actual.name = expected.name
    WHERE actual.column_id IS NULL OR actual.is_nullable <> 1
        OR TYPE_NAME(actual.system_type_id) <> expected.type_name
        OR actual.max_length <> expected.max_length
)
    THROW 51006, 'Payment correction columns are missing or have incorrect types or nullability.', 1;

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes idx
    INNER JOIN sys.index_columns key_column
        ON key_column.object_id = idx.object_id AND key_column.index_id = idx.index_id
    INNER JOIN sys.columns col
        ON col.object_id = key_column.object_id AND col.column_id = key_column.column_id
    WHERE idx.object_id = OBJECT_ID(N'dbo.Payments')
        AND idx.name = N'IX_Payments_CorrectionRequestId'
        AND idx.is_unique = 0 AND idx.is_disabled = 0 AND idx.has_filter = 0
        AND key_column.key_ordinal = 1 AND col.name = N'CorrectionRequestId'
)
    THROW 51007, 'Payment correction request index is missing or incorrect.', 1;

PRINT 'Payment correction schema verified: migration, five nullable columns and request index.';
