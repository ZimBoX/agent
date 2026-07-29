using ClaudeAssistant.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace ClaudeAssistant.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<EnrollmentCode> EnrollmentCodes => Set<EnrollmentCode>();
    public DbSet<Template> Templates => Set<Template>();
    public DbSet<RequestHistoryEntry> RequestHistory => Set<RequestHistoryEntry>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<ChatConversation> ChatConversations => Set<ChatConversation>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<GeneratedFileRecord> GeneratedFiles => Set<GeneratedFileRecord>();
    public DbSet<AiModel> AiModels => Set<AiModel>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Device>()
            .HasIndex(d => d.TokenHash)
            .IsUnique();

        modelBuilder.Entity<Role>()
            .HasIndex(r => r.Name)
            .IsUnique();

        modelBuilder.Entity<EnrollmentCode>()
            .HasIndex(c => c.Code)
            .IsUnique();

        modelBuilder.Entity<Device>()
            .HasOne(d => d.User)
            .WithMany(u => u.Devices)
            .HasForeignKey(d => d.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<EnrollmentCode>()
            .HasOne(c => c.User)
            .WithMany()
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<RequestHistoryEntry>()
            .HasOne(r => r.User)
            .WithMany(u => u.RequestHistory)
            .HasForeignKey(r => r.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<RequestHistoryEntry>()
            .HasOne(r => r.Template)
            .WithMany()
            .HasForeignKey(r => r.TemplateId)
            .OnDelete(DeleteBehavior.Restrict);

        // Многие-ко-многим через неявную join-таблицу TemplateRoles — шаблон
        // может относиться сразу к нескольким должностям/отделам.
        modelBuilder.Entity<Template>()
            .HasMany(t => t.Roles)
            .WithMany()
            .UsingEntity(j => j.ToTable("TemplateRoles"));

        modelBuilder.Entity<ChatConversation>()
            .HasOne(c => c.User)
            .WithMany()
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ChatMessage>()
            .HasOne(m => m.Conversation)
            .WithMany(c => c.Messages)
            .HasForeignKey(m => m.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Файл привязан к записи истории ИЛИ к сообщению чата; при удалении родителя
        // (в т.ч. каскадно при удалении пользователя/диалога) удаляется и файл.
        modelBuilder.Entity<GeneratedFileRecord>()
            .HasOne(f => f.User)
            .WithMany()
            .HasForeignKey(f => f.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<GeneratedFileRecord>()
            .HasOne(f => f.RequestHistoryEntry)
            .WithMany()
            .HasForeignKey(f => f.RequestHistoryEntryId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<GeneratedFileRecord>()
            .HasOne(f => f.ChatMessage)
            .WithMany()
            .HasForeignKey(f => f.ChatMessageId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
