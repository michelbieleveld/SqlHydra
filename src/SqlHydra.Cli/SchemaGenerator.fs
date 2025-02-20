﻿module SqlHydra.SchemaGenerator

open Domain
open Fantomas.Core
open Fantomas.FCS.Text
open Fabulous.AST
open type Ast

let range0 = range.Zero

let backticks = Fantomas.FCS.Syntax.PrettyNaming.NormalizeIdentifierBackticks

/// Creates a "HydraReader" class with properties for each table in a given schema.
let generateHydraReaderClass (db: Schema) (rdrCfg: ReadersConfig) (app: AppInfo) (allTables: Table seq) = 
    Class(
        "HydraReader", 
        Constructor(            
            ParenPat(ParameterPat("reader", LongIdent(rdrCfg.ReaderType)))
        )        
    ) {
        // Backing fields
        Value("accFieldCount", Unquoted "0").toMutable()

        for table in allTables do             
            // let lazyPersonEmailAddress = lazy (Person.Readers.EmailAddressReader(reader, buildGetOrdinal 5 typeof<Person.EmailAddress))
            // let lazypublicmigration = lazy (``public``.Readers.migrationReader(reader, buildGetOrdinal typeof<``public``.migration>))
            Value(
                $"lazy{table.Schema}{table.Name}", 
                ConstantExpr(Unquoted $"lazy ({backticks table.Schema}.Readers.{backticks table.Name}Reader(reader, buildGetOrdinal typeof<{backticks table.Schema}.{table.Name}>))")
            )

        for table in allTables do 
            // member __.``HumanResources.Department`` = lazyHumanResourcesDepartment.Value
            Property($"__.``{table.Schema}.{table.Name}``", Unquoted $"lazy{table.Schema}{table.Name}.Value")

        // member private __.AccFieldCount with get () = accFieldCount and set (value) = accFieldCount <- value
        // (Use a placeholder property until get/set properties are added to Fabulous.AST)
        Property("__.AccFieldCount", Unquoted "0")

        // Method: member private __.GetReaderByName(entity: string, isOption: bool) =
        Method(
            "__.GetReaderByName",
            ParenPat (
                TuplePat [
                    ParameterPat("entity", String())
                    ParameterPat("isOption", Boolean())
                ]
            ),

            // match entity, isOption with
            MatchExpr(
                TupleExpr [
                    ConstantExpr(Unquoted "entity")
                    ConstantExpr(Unquoted "isOption")
                ],
                [ 
                    for table in allTables do
                        // | "OT.CONTACTS", false -> __.``OT.CONTACTS``.Read >> box
                    
                        // match case: isOption = false
                        MatchClauseExpr(
                            TuplePat [
                                ConstantPat($"\"{table.Schema}.{table.Name}\"")
                                ConstantPat("false")
                            ], 
                            ConstantExpr(Unquoted $"__.``{table.Schema}.{table.Name}``.Read >> box")
                        )

                        // match case: isOption = true
                        MatchClauseExpr(
                            TuplePat [
                                ConstantPat($"\"{table.Schema}.{table.Name}\"")
                                ConstantPat("true")
                            ], 
                            ConstantExpr(Unquoted $"__.``{table.Schema}.{table.Name}``.ReadIfNotNull >> box")
                        )

                    //| _ -> failwith $"Could not read type '{entity}' because no generated reader exists."
                    MatchClauseExpr(
                        WildPat(), 
                        ConstantExpr(Unquoted "failwith $\"Could not read type '{entity}' because no generated reader exists.\"")
                    )
                ]
            )
        )
            .toPrivate()

        // Method: static member private GetPrimitiveReader(t: System.Type, reader: Microsoft.Data.SqlClient.SqlDataReader, isOpt: bool, isNullable: bool) =
        Method("GetPrimitiveReader", 
            ParenPat(
                TuplePat [
                    ParameterPat("t", LongIdent "System.Type")
                    ParameterPat("reader", LongIdent rdrCfg.ReaderType)
                    ParameterPat("isOpt", Boolean())
                    ParameterPat("isNullable", Boolean())
                ]
            ),

            let wrapFnName (ptr: PrimitiveTypeReader) = 
                if ptr.ClrType |> isValueType
                then "wrapValue"
                else "wrapRef"

            IfThenElifExpr( 
                [ for i, ptr in db.PrimitiveTypeReaders |> Seq.indexed do
                    
                    let readerGetFieldValueMethod =
                        if ptr.ClrType.EndsWith "[]"
                        then $"GetFieldValue<{ptr.ClrType}>" // handles array types
                        else $"{ptr.ReaderMethod}"
                    
                    let ifExpr = ConstantExpr(Unquoted $"t = typedefof<{ptr.ClrType}>")
                    let elExpr = ConstantExpr(Unquoted $"Some({wrapFnName ptr} reader.{readerGetFieldValueMethod})")

                    if i = 0 
                    then IfThenExpr(ifExpr, elExpr)
                    else ElIfThenExpr(ifExpr, elExpr) 
                ],
                ConstantExpr(Unquoted "None")
            )
        )
            .toPrivate()
            .toStatic()

        // static member Read(reader: Microsoft.Data.SqlClient.SqlDataReader) = 
        // (use a placeholder method)
        Method("Read", 
            ParenPat(
                ParameterPat("reader", LongIdent rdrCfg.ReaderType)
            ),
            ConstantExpr(Unquoted "// ReadMethodBodyPlaceholder")
        )
            .toStatic()
    }    

/// Generates the outer module and table records.
let generateNamespace (cfg: Config) (app: AppInfo) (db: Schema) = 
    let filteredTables = 
        db.Tables 
        |> List.sortBy (fun tbl -> tbl.Schema, tbl.Name)

    let schemas = 
        let enumSchemas = db.Enums |> List.map (fun e -> e.Schema)
        let tableSchemas = filteredTables |> List.map (fun t -> t.Schema) 
        enumSchemas @ tableSchemas |> List.distinct
    
    Namespace(cfg.Namespace) {

        Open "SqlHydra"
        Open "SqlHydra.Query.Table"

        if cfg.Readers.IsSome then 
            Open "Substitue.ColumnReadersModule"

        // Schema modules with enums, tables and readers
        for schema in schemas do
            let tables = 
                filteredTables 
                |> List.filter (fun t -> t.Schema = schema)

            let enums = 
                db.Enums 
                |> List.filter (fun e -> e.Schema = schema)
                |> List.map (fun e -> e.Name)

            // Add a module for each schema
            NestedModule(schema) {
                // Add enums in schema
                for enum in enums do
                    let enumType = 
                        db.Enums 
                        |> List.find (fun e -> e.Schema = schema && e.Name = enum)

                    let labels = 
                        enumType.Labels 
                        |> List.sortBy _.SortOrder
                                            
                    Enum(backticks enum) {
                        for label in labels do
                            EnumCase(backticks label.Name, string label.SortOrder)
                    }

                // Add tables in schema
                for table in tables do
                    let tableType = 
                        db.Tables 
                        |> List.find (fun t -> t.Schema = schema && t.Name = table.Name)

                    
                    let tableRecord = 
                        Record(table.Name) {
                        
                            for col in tableType.Columns do 
                                let baseType = 
                                    // Handles array types: "byte[]", "string[]", "int[]", "int []", "int array"
                                    if col.TypeMapping.ClrType.EndsWith "[]" || col.TypeMapping.ClrType.EndsWith "array" then
                                        let baseTypeNm = col.TypeMapping.ClrType.Split([| "[]"; " []"; " array" |], System.StringSplitOptions.RemoveEmptyEntries) |> Array.head
                                        $"{baseTypeNm} []"
                                    else
                                        col.TypeMapping.ClrType

                                let columnPropertyType =
                                    if col.IsNullable then
                                        match cfg.NullablePropertyType with
                                        | NullablePropertyType.Option ->
                                            $"Option<{baseType}>"
                                        | NullablePropertyType.Nullable ->
                                            $"System.Nullable<{baseType}>"
                                    else 
                                        baseType

                                let field = Field(col.Name, columnPropertyType)
                                match col.TypeMapping.ProviderDbType with
                                | Some providerDbType when cfg.ProviderDbTypeAttributes -> 
                                    field.attribute(Attribute($"ProviderDbType(\"{providerDbType}\")"))
                                | _ -> 
                                    field
                        }

                    if cfg.IsCLIMutable 
                    then tableRecord.attribute(Attribute("CLIMutable"))
                    else tableRecord

                    if cfg.TableDeclarations then
                        Value(table.Name, Unquoted $"table<{backticks table.Name}>")

                // Add "Readers" module if readers are enabled
                match cfg.Readers with
                | Some readers -> 
                    NestedModule("Readers") {
                        for table in tables do 
                            Class(
                                $"{backticks table.Name}Reader", 
                                Constructor(
                                    ParenPat(
                                        TuplePat [
                                            ParameterPat("reader", LongIdent readers.ReaderType)
                                            ParameterPat("getOrdinal")
                                        ]
                                    )
                                )
                            ) {
                                for col in table.Columns do

                                    let columnReaderType =
                                        if col.IsNullable then 
                                            match cfg.NullablePropertyType with
                                            | NullablePropertyType.Option ->
                                                "OptionColumn"                  // Returns None for DBNull.Value
                                            | NullablePropertyType.Nullable ->
                                                if col.TypeMapping.IsValueType() 
                                                then "NullableValueColumn"      // Returns System.Nullable<> for DBNull.Value
                                                else "NullableObjectColumn"     // Returns null for DBNull.Value
                                        else 
                                            "RequiredColumn"

                                    Property($"__.{backticks col.Name}", Unquoted $"{columnReaderType}(reader, getOrdinal, reader.{col.TypeMapping.ReaderMethod}, \"{col.Name}\")")

                                Method(
                                    "__.Read", 
                                    UnitPat(),                                     
                                    let recordExpr = 
                                        RecordExpr [
                                            for col in table.Columns do
                                                RecordFieldExpr(backticks col.Name, ConstantExpr(Unquoted $"__.{backticks col.Name}.Read()"))
                                        ]

                                    TypedExpr(recordExpr, ":", LongIdent(backticks table.Name))                                    
                                )

                                Method(
                                    "__.ReadIfNotNull",
                                    UnitPat(),

                                    // Try to get the first PK, or else the first required field, or else the first optional field (as a last resort)
                                    let firstPkOrFirstRequiredField = 
                                        let firstRequiredField = table.Columns |> Seq.tryFind (fun c -> c.IsNullable = false)
                                        let firstOptionalField = table.Columns |> Seq.tryFind (fun c -> c.IsNullable = true)
                                        table.Columns 
                                        |> List.tryFind (fun c -> c.IsPK)
                                        |> Option.orElse firstRequiredField
                                        |> Option.orElse firstOptionalField
                                        |> Option.map (fun c -> c.Name)
                                    
                                    // If at least one PK column exists, check first PK for null; else check user supplied column arg for null.
                                    match firstPkOrFirstRequiredField with
                                    | Some pkCol -> 
                                        //LongIdentWithDots.Create([ "__"; col; "IsNull" ])

                                        // if __.BusinessEntityID.IsNull() then None else Some(__.Read())
                                        IfThenElseExpr(
                                            ConstantExpr(Unquoted $"__.{backticks pkCol}.IsNull()"), 
                                            ConstantExpr(Unquoted "None"), 
                                            ConstantExpr(Unquoted "Some(__.Read())")
                                        )
                                    | None -> 
                                        ConstantExpr(Unquoted "None")
                                )
                            }
                    }
                | _ -> 
                    ()
            }
    
        // Create "HydraReader" below all generated tables/readers...
        if cfg.Readers.IsSome then
            let allTables = schemas |> List.collect (fun schema -> filteredTables |> List.filter (fun t -> t.Schema = schema))
            generateHydraReaderClass db cfg.Readers.Value app allTables
    }

/// Generates the entire schema.
let generate (cfg: Config) (app: AppInfo) (db: Schema) = 
    Oak() {
        generateNamespace cfg app db
    }
    

let columnReadersModule = $"""
[<AutoOpen>]
module ColumnReaders =
    type Column(reader: System.Data.IDataReader, getOrdinal: string -> int, column) =
            member __.Name = column
            member __.IsNull() = getOrdinal column |> reader.IsDBNull
            override __.ToString() = __.Name

    type RequiredColumn<'T, 'Reader when 'Reader :> System.Data.IDataReader>(reader: 'Reader, getOrdinal, getter: int -> 'T, column) =
            inherit Column(reader, getOrdinal, column)
            member __.Read(?alias) = alias |> Option.defaultValue __.Name |> getOrdinal |> getter

    type OptionColumn<'T, 'Reader when 'Reader :> System.Data.IDataReader>(reader: 'Reader, getOrdinal, getter: int -> 'T, column) =
            inherit Column(reader, getOrdinal, column)
            member __.Read(?alias) = 
                match alias |> Option.defaultValue __.Name |> getOrdinal with
                | o when reader.IsDBNull o -> None
                | o -> Some (getter o)

    type NullableObjectColumn<'T, 'Reader when 'Reader :> System.Data.IDataReader>(reader: 'Reader, getOrdinal, getter: int -> 'T, column) =
            inherit Column(reader, getOrdinal, column)
            member __.Read(?alias) = 
                match alias |> Option.defaultValue __.Name |> getOrdinal with
                | o when reader.IsDBNull o -> null
                | o -> (getter o) |> unbox

    type NullableValueColumn<'T, 'Reader when 'T : struct and 'T : (new : unit -> 'T) and 'T :> System.ValueType and 'Reader :> System.Data.IDataReader>(reader: 'Reader, getOrdinal, getter: int -> 'T, column) =
            inherit Column(reader, getOrdinal, column)
            member __.Read(?alias) = 
                match alias |> Option.defaultValue __.Name |> getOrdinal with
                | o when reader.IsDBNull o -> System.Nullable<'T>()
                | o -> System.Nullable<'T> (getter o)

[<AutoOpen>]
module private DataReaderExtensions =
    type System.Data.IDataReader with
        member reader.GetDateOnly(ordinal: int) = 
            reader.GetDateTime(ordinal) |> System.DateOnly.FromDateTime
    
    type System.Data.Common.DbDataReader with
        member reader.GetTimeOnly(ordinal: int) = 
            reader.GetFieldValue(ordinal) |> System.TimeOnly.FromTimeSpan
        """

/// A list of static code text substitutions to the generated file.
let substitutions (app: AppInfo) : (string * string) list = 
    [
        // Reader classes at top of namespace
        "open Substitue.ColumnReadersModule", columnReadersModule

        // HydraReader utility functions
        "let mutable accFieldCount = 0", 
        """let mutable accFieldCount = 0
    let buildGetOrdinal tableType =
        let fieldNames = 
            FSharp.Reflection.FSharpType.GetRecordFields(tableType)
            |> Array.map _.Name

        let dictionary = 
            [| 0 .. reader.FieldCount - 1 |] 
            |> Array.map (fun i -> reader.GetName(i), i)
            |> Array.sortBy snd
            |> Array.skip accFieldCount
            |> Array.filter (fun (name, _) -> Array.contains name fieldNames)
            |> Array.take fieldNames.Length
            |> dict
        accFieldCount <- accFieldCount + fieldNames.Length
        fun col -> dictionary.Item col
        """

        // GetPrimitiveReader method - let bindings
        "isOpt: bool, isNullable: bool) =",
        """isOpt: bool, isNullable: bool) =
        let wrapValue get (ord: int) = 
            if isOpt then (if reader.IsDBNull ord then None else get ord |> Some) |> box 
            elif isNullable then (if reader.IsDBNull ord then System.Nullable() else get ord |> System.Nullable) |> box
            else get ord |> box

        let wrapRef get (ord: int) = 
            if isOpt then (if reader.IsDBNull ord then None else get ord |> Some) |> box 
            else get ord |> box
        """

        // HydraReader class AccFieldCount property
        "member __.AccFieldCount = 0",
        "member private __.AccFieldCount with get () = accFieldCount and set (value) = accFieldCount <- value"

        // HydraReader Read Method Body
        "// ReadMethodBodyPlaceholder",
        $"""
        let hydra = HydraReader(reader)
        {if app.Name = "SqlHydra.Oracle" then "reader.SuppressGetDecimalInvalidCastException <- true" else ""}            
        let getOrdinalAndIncrement() = 
            let ordinal = hydra.AccFieldCount
            hydra.AccFieldCount <- hydra.AccFieldCount + 1
            ordinal
            
        let buildEntityReadFn (t: System.Type) = 
            let t, isOpt, isNullable = 
                if t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<Option<_>> then t.GenericTypeArguments[0], true, false
                elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<System.Nullable<_>> then t.GenericTypeArguments[0], false, true
                else t, false, false
            
            match HydraReader.GetPrimitiveReader(t, reader, isOpt, isNullable) with
            | Some primitiveReader -> 
                let ord = getOrdinalAndIncrement()
                fun () -> primitiveReader ord
            | None ->
                let nameParts = t.FullName.Split([| '.'; '+' |])
                let schemaAndType = nameParts |> Array.skip (nameParts.Length - 2) |> fun parts -> System.String.Join(".", parts)
                hydra.GetReaderByName(schemaAndType, isOpt)
            
        // Return a fn that will hydrate 'T (which may be a tuple)
        // This fn will be called once per each record returned by the data reader.
        let t = typeof<'T>
        if FSharp.Reflection.FSharpType.IsTuple(t) then
            let readEntityFns = FSharp.Reflection.FSharpType.GetTupleElements(t) |> Array.map buildEntityReadFn
            fun () ->
                let entities = readEntityFns |> Array.map (fun read -> read())
                Microsoft.FSharp.Reflection.FSharpValue.MakeTuple(entities, t) :?> 'T
        else
            let readEntityFn = t |> buildEntityReadFn
            fun () -> 
                readEntityFn() :?> 'T
        """
    ]

/// Formats the generated code using Fantomas.
let toFormattedCode (cfg: Config) (app: AppInfo) (version: string) (ast: WidgetBuilder<SyntaxOak.Oak>) = 
    let comment = $"// This code was generated by `{app.Name}` -- v{version}."

    let cfg = 
        { FormatConfig.Default with 
            FormatConfig.MaxIfThenElseShortWidth = 400           // Forces ReadIfNotNull if/then to be on a single line
            FormatConfig.MaxValueBindingWidth = 400              // Ensure reader property/column bindings stay on one line
            FormatConfig.MaxLineLength = 400                     // Ensure reader property/column bindings stay on one line
        }

    let formattedCode = 
        ast
        |> Gen.mkOak
        |> fun oak -> CodeFormatter.FormatOakAsync(oak, cfg)
        |> Async.RunSynchronously

    let finalCode = substitutions app |> List.fold (fun (code: string) (placeholder, sub) -> code.Replace(placeholder, sub)) formattedCode

    let formattedCodeWithComment =
        [   
            comment
            
            //if cfg.Readers.IsSome then
            //    columnReadersModule

            finalCode
        ]
        |> String.concat System.Environment.NewLine

    formattedCodeWithComment
