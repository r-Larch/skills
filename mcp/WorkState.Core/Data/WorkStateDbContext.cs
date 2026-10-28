using Microsoft.EntityFrameworkCore;
using OpenIddict.EntityFrameworkCore.Models;
using WorkState.Core.Domain;

namespace WorkState.Core.Data;

public class WorkStateDbContext(DbContextOptions<WorkStateDbContext> options) : DbContext(options)
{
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<ItemKind> ItemKinds => Set<ItemKind>();
    public DbSet<Item> Items => Set<Item>();
    public DbSet<ProjectConfig> ProjectConfigs => Set<ProjectConfig>();
    public DbSet<ProjectRepo> ProjectRepos => Set<ProjectRepo>();
    public DbSet<RunConfig> RunConfigs => Set<RunConfig>();
    public DbSet<ItemCriterion> ItemCriteria => Set<ItemCriterion>();
    public DbSet<ItemLink> ItemLinks => Set<ItemLink>();
    public DbSet<Doc> Docs => Set<Doc>();
    public DbSet<DocBlock> DocBlocks => Set<DocBlock>();
    public DbSet<DocBlockRevision> DocBlockRevisions => Set<DocBlockRevision>();
    public DbSet<Attempt> Attempts => Set<Attempt>();
    public DbSet<Report> Reports => Set<Report>();
    public DbSet<Verification> Verifications => Set<Verification>();
    public DbSet<Artifact> Artifacts => Set<Artifact>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<Assumption> Assumptions => Set<Assumption>();
    public DbSet<ConstraintRule> ConstraintRules => Set<ConstraintRule>();
    public DbSet<Note> Notes => Set<Note>();
    public DbSet<Claim> Claims => Set<Claim>();
    public DbSet<AgentToken> AgentTokens => Set<AgentToken>();
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();
    public DbSet<AgentSession> AgentSessions => Set<AgentSession>();
    public DbSet<ImportRecord> ImportRecords => Set<ImportRecord>();
    public DbSet<OAuthKey> OAuthKeys => Set<OAuthKey>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<TenantMembership> TenantMemberships => Set<TenantMembership>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasPostgresExtension("pg_trgm");
        b.HasPostgresExtension("btree_gin");

        // claim_one_session_per_tree is an exclusion constraint over text and uuid columns, which
        // only a GiST operator class from this extension can index. It is raw SQL in the Sessions
        // migration because the model cannot express an exclusion constraint.
        b.HasPostgresExtension("btree_gist");

        MapWorkspace(b);
        MapItemKind(b);
        MapItem(b);
        MapConfigs(b);
        MapCriteria(b);
        MapLinks(b);
        MapDocs(b);
        MapEvidence(b);
        MapJournal(b);
        MapThreads(b);
        MapDataProtection(b);
        MapSessions(b);
        MapOAuth(b);
        MapUsers(b);
    }

    private static void MapUsers(ModelBuilder b)
    {
        // app_user_name_unique, on lower(name), is raw SQL in the Users migration: the model cannot
        // express an expression index.
        b.Entity<AppUser>(e =>
        {
            e.ToTable("app_user");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.IsSystemAdmin).HasDefaultValue(false);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        });

        b.Entity<TenantMembership>(e =>
        {
            e.ToTable("tenant_membership");
            e.HasKey(x => new { x.UserId, x.WorkspaceId });
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.WorkspaceId).HasDatabaseName("tenant_membership_workspace_idx");
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.ToTable(t => t.HasCheckConstraint("tenant_membership_role_known", "role IN ('admin', 'member')"));
        });
    }

    /// <summary>
    /// OpenIddict's EF stores, under snake_case names like every other table here; its own defaults are
    /// PascalCase. Nothing in this schema references them and they reference nothing in it.
    /// </summary>
    private static void MapOAuth(ModelBuilder b)
    {
        b.UseOpenIddict();
        b.Entity<OpenIddictEntityFrameworkCoreApplication>().ToTable("openiddict_application");
        b.Entity<OpenIddictEntityFrameworkCoreAuthorization>().ToTable("openiddict_authorization");
        b.Entity<OpenIddictEntityFrameworkCoreScope>().ToTable("openiddict_scope");
        b.Entity<OpenIddictEntityFrameworkCoreToken>().ToTable("openiddict_token");

        b.Entity<OAuthKey>(e =>
        {
            e.ToTable("oauth_key");
            e.HasKey(x => x.Purpose);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.ToTable(t => t.HasCheckConstraint("oauth_key_purpose_known", "purpose IN ('signing', 'encryption')"));
        });
    }

    private static void MapSessions(ModelBuilder b)
    {
        b.Entity<AgentSession>(e =>
        {
            e.ToTable("agent_session");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.FirstSeenAt).HasDefaultValueSql("now()");
            e.Property(x => x.LastSeenAt).HasDefaultValueSql("now()");
            e.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.WorkspaceId, x.Name })
                .HasDatabaseName("agent_session_name_unique")
                .IsUnique();
            e.HasIndex(x => x.TokenHash)
                .HasDatabaseName("agent_session_token_hash_unique")
                .IsUnique();
            e.HasIndex(x => new { x.WorkspaceId, x.ClientKey })
                .HasDatabaseName("agent_session_client_key_unique")
                .IsUnique()
                .HasFilter("client_key IS NOT NULL");
            e.HasOne<AgentSession>().WithMany().HasForeignKey(x => x.MintedBySessionId).OnDelete(DeleteBehavior.SetNull);
            e.Property(x => x.Grants).HasDefaultValueSql("'{}'::text[]");
            e.ToTable(t => t.HasCheckConstraint("agent_session_grants_known", "grants <@ ARRAY['import']::text[]"));
        });

        b.Entity<ImportRecord>(e =>
        {
            e.ToTable("import_record");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.SourceAnchor).HasDefaultValue("");
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
            e.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Item>().WithMany().HasForeignKey(x => x.ProjectItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Note>().WithMany().HasForeignKey(x => x.NoteId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Assumption>().WithMany().HasForeignKey(x => x.AssumptionId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<ConstraintRule>().WithMany().HasForeignKey(x => x.ConstraintId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AgentSession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => new { x.ProjectItemId, x.SourcePath, x.SourceAnchor })
                .HasDatabaseName("import_record_source_unique")
                .IsUnique();
            e.HasIndex(x => x.ItemId).HasDatabaseName("import_record_item_idx");
            e.ToTable(t =>
            {
                t.HasCheckConstraint(
                    "import_record_one_target",
                    "num_nonnulls(item_id, note_id, assumption_id, constraint_id) = 1");
                t.HasCheckConstraint(
                    "import_record_kind",
                    "kind IN ('run','phase','task','followup','note','assumption','rule')");
            });
        });

        // Additive: every row written before doc 11 keeps a null session, and nothing backfills one.
        b.Entity<Event>().HasOne<AgentSession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<Claim>().HasOne<AgentSession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<Verification>().HasOne<AgentSession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<Report>().HasOne<AgentSession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<Note>().HasOne<AgentSession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<Assumption>().HasOne<AgentSession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.SetNull);
    }

    private static void MapDataProtection(ModelBuilder b) => b.Entity<DataProtectionKey>(e =>
    {
        e.ToTable("data_protection_key");
        e.HasKey(x => x.Id);
        e.Property(x => x.Id).UseIdentityAlwaysColumn();
    });

    private static void MapThreads(ModelBuilder b)
    {
        b.Entity<Assumption>(e =>
        {
            e.ToTable("assumption");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.State).HasDefaultValue(AssumptionState.Open);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.Property(x => x.RefScopeId).ValueGeneratedOnAddOrUpdate();
            e.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Item>().WithMany().HasForeignKey(x => x.ScopeItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Item>().WithMany().HasForeignKey(x => x.RefScopeId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<Item>().WithMany().HasForeignKey(x => x.RevisitItemId).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => x.WorkspaceId)
                .HasDatabaseName("assumption_open_idx")
                .HasFilter("state = 'open'");
            e.ToTable(t => t.HasCheckConstraint(
                "assumption_resolved",
                "(state = 'open') = (resolved_at IS NULL)"));
        });

        b.Entity<ConstraintRule>(e =>
        {
            e.ToTable("constraint_rule");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.CoversRisk).HasDefaultValueSql("'{}'::text[]");
            e.Property(x => x.Severity).HasDefaultValue("binding");
            e.Property(x => x.Active).HasDefaultValue(true);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Item>().WithMany().HasForeignKey(x => x.ScopeItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Item>().WithMany().HasForeignKey(x => x.AddedByItemId).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => new { x.WorkspaceId, x.ScopeItemId })
                .HasDatabaseName("constraint_scope_idx")
                .HasFilter("active");
            e.HasIndex(x => x.CoversRisk).HasDatabaseName("constraint_risk_idx").HasMethod("gin");
            e.HasIndex(x => new { x.WorkspaceId, x.Ref })
                .HasDatabaseName("constraint_ref_unique")
                .IsUnique();
        });

        b.Entity<Note>(e =>
        {
            e.ToTable("note");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.ToSelector).HasColumnType("jsonb");
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Item>().WithMany().HasForeignKey(x => x.FromItemId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<Item>().WithMany().HasForeignKey(x => x.ToItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Note>().WithMany().HasForeignKey(x => x.CorrectsNoteId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<DocBlock>().WithMany().HasForeignKey(x => x.CorrectsBlockId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<Attempt>().WithMany().HasForeignKey(x => x.ConsumedBy).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => new { x.WorkspaceId, x.ToItemId })
                .HasDatabaseName("note_pending_idx")
                .HasFilter("consumed_at IS NULL");
            e.HasIndex(x => x.ToSelector)
                .HasDatabaseName("note_selector_idx")
                .HasMethod("gin")
                .HasOperators("jsonb_path_ops")
                .HasFilter("consumed_at IS NULL");
        });

        b.Entity<Claim>(e =>
        {
            e.ToTable("claim");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Writes).HasDefaultValue(true);
            e.Property(x => x.AcquiredAt).HasDefaultValueSql("now()");
            e.Property(x => x.HeartbeatAt).HasDefaultValueSql("now()");
            e.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AgentToken>().WithMany().HasForeignKey(x => x.AgentTokenId).OnDelete(DeleteBehavior.SetNull);

            e.HasIndex(x => x.ItemId)
                .HasDatabaseName("claim_one_live_per_item")
                .IsUnique()
                .HasFilter("released_at IS NULL");

            // One writer per working tree. Two units in separate worktrees proceed in parallel; two
            // sharing a tree do not. The parallel-orchestrator rule, as an index. A null
            // key_workspace_id is a path and collides across workspaces — NULLS NOT DISTINCT.
            // doc 11 narrows it to claims with no session; claim_one_session_per_tree (Sessions
            // migration) lets one session hold several items of its tree and refuses a second one.
            e.HasIndex(x => new { x.SerializationKey, x.KeyWorkspaceId })
                .HasDatabaseName("claim_one_writer_per_tree")
                .IsUnique()
                .AreNullsDistinct(false)
                .HasFilter("released_at IS NULL AND writes AND session_id IS NULL");
        });

        b.Entity<AgentToken>(e =>
        {
            e.ToTable("agent_token");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.IssuedAt).HasDefaultValueSql("now()");
            e.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Item>().WithMany().HasForeignKey(x => x.ScopeItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.TokenHash).HasDatabaseName("agent_token_hash_idx").IsUnique();
        });
    }

    private static void MapWorkspace(ModelBuilder b) => b.Entity<Workspace>(e =>
    {
        e.ToTable("workspace");
        e.HasKey(x => x.Id);
        e.Property(x => x.Id).ValueGeneratedNever();
        e.Property(x => x.Slug).HasDefaultValue("default");
        e.HasIndex(x => x.Slug).IsUnique();
        e.Property(x => x.Settings).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
        e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
    });

    private static void MapItemKind(ModelBuilder b) => b.Entity<ItemKind>(e =>
    {
        e.ToTable("item_kind");
        e.HasKey(x => x.Key);
        e.Property(x => x.Key).ValueGeneratedNever();
        e.Property(x => x.AllowedParentKinds).HasDefaultValueSql("'{}'::text[]");
    });

    private static void MapItem(ModelBuilder b) => b.Entity<Item>(e =>
    {
        e.ToTable("item");
        e.HasKey(x => x.Id);
        e.Property(x => x.Id).ValueGeneratedNever();

        e.Property(x => x.Seq).HasColumnType("numeric");

        // Trigger-owned: never written by the application, read back through RETURNING.
        e.Property(x => x.Ancestors).HasDefaultValueSql("'{}'::uuid[]").ValueGeneratedOnAddOrUpdate();
        e.Property(x => x.RefScopeId).ValueGeneratedOnAddOrUpdate();

        e.Property(x => x.Depth)
            .HasComputedColumnSql("coalesce(array_length(ancestors, 1), 0)", stored: true);
        e.Property(x => x.RiskTags).HasDefaultValueSql("'{}'::text[]");
        e.Property(x => x.ReviewRequired)
            .HasComputedColumnSql("risk_tags <> '{}'::text[]", stored: true);
        e.Property(x => x.Status).HasDefaultValue(ItemStatus.Open);
        e.Property(x => x.Priority).HasDefaultValue((short)0);
        e.Property(x => x.Revision).HasDefaultValue(1);
        e.Property(x => x.OpenedAt).HasDefaultValueSql("now()");
        e.Property(x => x.Search).HasComputedColumnSql(
            """
            setweight(to_tsvector('simple', coalesce(ref, '')), 'A') ||
            setweight(to_tsvector('english', coalesce(title, '')), 'B') ||
            setweight(to_tsvector('english', coalesce(intent, '')), 'C')
            """,
            stored: true);

        e.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);
        e.HasOne<Item>().WithMany().HasForeignKey(x => x.ParentId)
            .OnDelete(DeleteBehavior.Restrict);
        e.HasOne<Item>().WithMany().HasForeignKey(x => x.RefScopeId)
            .OnDelete(DeleteBehavior.NoAction);
        e.HasOne<ItemKind>().WithMany().HasForeignKey(x => x.Kind)
            .OnDelete(DeleteBehavior.Restrict);
        e.HasOne<Doc>().WithMany().HasForeignKey(x => x.BodyDocId)
            .OnDelete(DeleteBehavior.SetNull);

        e.HasIndex(x => x.Ancestors).HasDatabaseName("item_subtree_idx").HasMethod("gin");
        e.HasIndex(x => new { x.WorkspaceId, x.Status, x.Seq })
            .HasDatabaseName("item_open_idx")
            .HasFilter("status IN ('open','ready','in_progress','blocked','review')");
        e.HasIndex(x => new { x.ParentId, x.Seq }).HasDatabaseName("item_parent_seq_idx");
        e.HasIndex(x => x.Search).HasDatabaseName("item_search_idx").HasMethod("gin");
        e.HasIndex(x => x.Ref).HasDatabaseName("item_ref_trgm_idx")
            .HasMethod("gin").HasOperators("gin_trgm_ops");
        e.HasIndex(x => x.RiskTags).HasDatabaseName("item_risk_idx").HasMethod("gin");

        e.ToTable(t =>
        {
            t.HasCheckConstraint(
                "item_resolution_only_when_done",
                "(status = 'done') = (resolution IS NOT NULL)");
            t.HasCheckConstraint(
                "item_closed_at_set",
                "(status IN ('done','cancelled','superseded')) = (closed_at IS NOT NULL)");
            t.HasCheckConstraint(
                "item_archived_only_project",
                "archived_at IS NULL OR kind = 'project'");
        });
    });

    private static void MapConfigs(ModelBuilder b)
    {
        b.Entity<ProjectConfig>(e =>
        {
            e.ToTable("project_config");
            e.HasKey(x => x.ItemId);
            e.Property(x => x.ItemId).ValueGeneratedNever();
            e.Property(x => x.DefaultBranch).HasDefaultValue("master");
            e.Property(x => x.VerifyCatalog).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            e.HasOne<Item>().WithOne().HasForeignKey<ProjectConfig>(x => x.ItemId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ProjectRepo>(e =>
        {
            e.ToTable("project_repo");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.AddedAt).HasDefaultValueSql("now()");
            e.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Item>().WithMany().HasForeignKey(x => x.ProjectItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.WorkspaceId, x.Key })
                .HasDatabaseName("project_repo_key_unique")
                .IsUnique();
            e.HasIndex(x => x.ProjectItemId).HasDatabaseName("project_repo_project_idx");
            e.ToTable(t => t.HasCheckConstraint("project_repo_kind", "kind IN ('remote','root')"));
        });

        b.Entity<RunConfig>(e =>
        {
            e.ToTable("run_config");
            e.HasKey(x => x.ItemId);
            e.Property(x => x.ItemId).ValueGeneratedNever();
            e.Property(x => x.Mode).HasDefaultValue("full");
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.HasOne<Item>().WithOne().HasForeignKey<RunConfig>(x => x.ItemId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Item>().WithMany().HasForeignKey(x => x.ProjectItemId)
                .OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => x.SerializationKey).HasDatabaseName("run_serialization_idx");
        });
    }

    private static void MapCriteria(ModelBuilder b) => b.Entity<ItemCriterion>(e =>
    {
        e.ToTable("item_criterion");
        e.HasKey(x => x.Id);
        e.Property(x => x.Id).ValueGeneratedNever();
        e.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne<Verification>().WithMany().HasForeignKey(x => x.EvidenceVerificationId)
            .HasConstraintName("item_criterion_evidence_fk")
            .OnDelete(DeleteBehavior.SetNull);
        e.HasIndex(x => new { x.ItemId, x.Seq }).IsUnique();
        e.HasIndex(x => x.ItemId).HasDatabaseName("item_criterion_open_idx").HasFilter("NOT checked");
    });

    private static void MapEvidence(ModelBuilder b)
    {
        b.Entity<Attempt>(e =>
        {
            e.ToTable("attempt");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.StartedAt).HasDefaultValueSql("now()");
            e.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Doc>().WithMany().HasForeignKey(x => x.BriefDocId).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => new { x.ItemId, x.N }).IsUnique();
        });

        b.Entity<Report>(e =>
        {
            e.ToTable("report");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Files).HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb");
            e.Property(x => x.At).HasDefaultValueSql("now()");
            e.HasOne<Attempt>().WithMany().HasForeignKey(x => x.AttemptId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.AttemptId).IsUnique();
        });

        b.Entity<Verification>(e =>
        {
            e.ToTable("verification");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Counts).HasColumnType("jsonb");
            e.Property(x => x.Samples).HasColumnType("jsonb");
            e.Property(x => x.RedCheck).HasColumnType("jsonb");
            e.Property(x => x.At).HasDefaultValueSql("now()");
            e.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Attempt>().WithMany().HasForeignKey(x => x.AttemptId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => new { x.ItemId, x.At })
                .HasDatabaseName("verification_item_idx")
                .IsDescending(false, true);

            // Answers plan-audit check #1 without an agent: has this command ever been recorded red?
            e.HasIndex(x => new { x.WorkspaceId, x.Command, x.Passed })
                .HasDatabaseName("verification_cmd_idx");
        });

        b.Entity<Artifact>(e =>
        {
            e.ToTable("artifact");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Meta).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            e.Property(x => x.At).HasDefaultValueSql("now()");
            e.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ItemId, x.Kind }).HasDatabaseName("artifact_item_idx");
        });
    }

    private static void MapJournal(ModelBuilder b) => b.Entity<Event>(e =>
    {
        e.ToTable("event");
        e.HasKey(x => x.Seq);
        e.Property(x => x.Seq).ValueGeneratedOnAdd().UseIdentityAlwaysColumn();
        e.Property(x => x.At).HasDefaultValueSql("now()");
        e.Property(x => x.Payload).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
        e.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Cascade);
        e.HasIndex(x => new { x.WorkspaceId, x.Seq })
            .HasDatabaseName("event_ws_seq_idx")
            .IsDescending(false, true);
        e.HasIndex(x => new { x.ItemId, x.Seq })
            .HasDatabaseName("event_item_seq_idx")
            .IsDescending(false, true);
    });

    private static void MapLinks(ModelBuilder b) => b.Entity<ItemLink>(e =>
    {
        e.ToTable("item_link");
        e.HasKey(x => x.Id);
        e.Property(x => x.Id).ValueGeneratedNever();
        e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        e.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);
        e.HasOne<Item>().WithMany().HasForeignKey(x => x.FromItemId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne<Item>().WithMany().HasForeignKey(x => x.ToItemId).OnDelete(DeleteBehavior.Cascade);
        e.HasIndex(x => new { x.FromItemId, x.ToItemId, x.Type })
            .HasDatabaseName("item_link_unique").IsUnique();
        e.HasIndex(x => new { x.ToItemId, x.Type }).HasDatabaseName("item_link_to_idx");
        e.HasIndex(x => new { x.FromItemId, x.Type }).HasDatabaseName("item_link_from_idx");
        e.ToTable(t => t.HasCheckConstraint("item_link_no_self", "from_item_id <> to_item_id"));
    });

    private static void MapDocs(ModelBuilder b)
    {
        b.Entity<Doc>(e =>
        {
            e.ToTable("doc");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Revision).HasDefaultValue(1);
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
            e.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.WorkspaceId, x.ItemId, x.Slug }).IsUnique();
        });

        b.Entity<DocBlock>(e =>
        {
            e.ToTable("doc_block");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Seq).HasColumnType("numeric");
            e.Property(x => x.Body).HasDefaultValue("");
            e.Property(x => x.Revision).HasDefaultValue(1);
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
            e.Property(x => x.Search).HasComputedColumnSql(
                """
                setweight(to_tsvector('english', coalesce(heading, '')), 'A') ||
                setweight(to_tsvector('english', body), 'B')
                """,
                stored: true);
            e.HasOne<Doc>().WithMany().HasForeignKey(x => x.DocId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.DocId, x.ShortId }).IsUnique();
            e.HasIndex(x => x.Search).HasDatabaseName("doc_block_search_idx").HasMethod("gin");
        });

        b.Entity<DocBlockRevision>(e =>
        {
            e.ToTable("doc_block_revision");
            e.HasKey(x => new { x.BlockId, x.Revision });
            e.Property(x => x.At).HasDefaultValueSql("now()");
            e.HasOne<DocBlock>().WithMany().HasForeignKey(x => x.BlockId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
