using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace ChannelWave.Storage.Sqlite;

[DbContext(typeof(LibraryDb))]
public sealed class LibraryDbModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder model)
    {
        model.HasAnnotation("ProductVersion", "10.0.12");
        ListeningSchema.Configure(model);
        model.Entity("ChannelWave.Storage.Sqlite.TrackRow", row =>
        {
            row.Property<long>("Account").HasColumnType("INTEGER");
            row.Property<string>("Identity").IsRequired().HasColumnType("TEXT");
            row.Property<Guid>("Id").HasColumnType("TEXT");
            row.Property<string>("SearchText").IsRequired().HasColumnType("TEXT");
            row.Property<string>("Json").IsRequired().HasColumnType("TEXT");
            row.HasKey("Account", "Identity");
            row.HasIndex("Account", "Id").IsUnique();
            row.ToTable("Tracks");
        });
        model.Entity("ChannelWave.Storage.Sqlite.DocumentRow", row =>
        {
            row.Property<long>("Account").HasColumnType("INTEGER");
            row.Property<string>("Key").IsRequired().HasColumnType("TEXT");
            row.Property<string>("Json").IsRequired().HasColumnType("TEXT");
            row.HasKey("Account", "Key");
            row.ToTable("Documents");
        });
    }
}
