; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
QRY001 | Queryeables | Error | Entity without key
QRY002 | Queryeables | Error | [EntityGraph] on a property that is not a writable collection of entities
QRY003 | Queryeables | Error | Foreign key not found
QRY004 | Queryeables | Error | Property named by [EntityGraph] not found
QRY005 | Queryeables | Error | Foreign key of the wrong type
QRY006 | Queryeables | Error | Lookup key not found
QRY007 | Queryeables | Error | Lookup key of the wrong type
QRY008 | Queryeables | Error | ParentNavigation of the wrong type
QRY009 | Queryeables | Error | LookupForeignKey is the key of the child
