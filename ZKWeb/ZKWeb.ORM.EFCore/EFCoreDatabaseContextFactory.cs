﻿using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Linq;
using ZKWeb.Database;
using Microsoft.EntityFrameworkCore.Migrations.Design;
using Microsoft.CodeAnalysis.CSharp;
using ZKWeb.Plugin.CompilerServices;
using System.IO;
using ZKWeb.Plugin.AssemblyLoaders;
using System.Reflection;

namespace ZKWeb.ORM.EFCore {
	/// <summary>
	/// Entity Framework Core database context factory<br/>
	/// Entity Framework Core的数据库上下文生成器<br/>
	/// </summary>
	public class EFCoreDatabaseContextFactory : IDatabaseContextFactory {
		/// <summary>
		/// Filename prefix for model snapshot<br/>
		/// 模型快照的文件名前缀<br/>
		/// </summary>
		protected const string ModelSnapshotFilePrefix = "EFModelSnapshot_";
		/// <summary>
		/// Namespace for model snapshot<br/>
		/// 模型快照的命名空间<br/>
		/// </summary>
		protected const string ModelSnapshotNamespace = "ZKWeb.ORM.EFCore.Migrations";
		/// <summary>
		/// Class name prefix for model snapshot<br/>
		/// 模型快照的类名前缀<br/>
		/// </summary>
		protected const string ModelSnapshotClassPrefix = "Migration_";
		/// <summary>
		/// Database type<br/>
		/// 数据库类型<br/>
		/// </summary>
		protected string Database { get; set; }
		/// <summary>
		/// Connection string<br/>
		/// 连接字符串<br/>
		/// </summary>
		protected string ConnectionString { get; set; }

		/// <summary>
		/// Initialize<br/>
		/// 初始化<br/>
		/// </summary>
		/// <param name="database">Database type</param>
		/// <param name="connectionString">Connection string</param>
		public EFCoreDatabaseContextFactory(string database, string connectionString) {
			Database = database;
			ConnectionString = connectionString;
			// Prepare database migration
			IModel initialModel = null;
			using (var context = new EFCoreDatabaseContextBase(Database, ConnectionString)) {
				// We may need create a new database and migration history table
				// It's done here
				context.Database.EnsureCreated();
				initialModel = context.Model;
			}
			// Perform database migration
			using (var context = new EFCoreDatabaseContext(Database, ConnectionString)) {
				var serviceProvider = ((IInfrastructure<IServiceProvider>)context).Instance;
				var databaseCreator = serviceProvider.GetService<IDatabaseCreator>();
				if (databaseCreator is IRelationalDatabaseCreator) {
					// It's a relational database, create and apply the migration
					MigrateRelationalDatabase(context, initialModel);
				} else {
					// It maybe an in-memory database or no-sql database, do nothing
				}
			}
		}

		/// <summary>
		/// Create and apply the migration for relational database<br/>
		/// 创建并且迁移数据库<br/>
		/// See: https://github.com/aspnet/EntityFramework/blob/master/src/Microsoft.EntityFrameworkCore.Relational/Storage/RelationalDatabaseCreator.cs
		/// </summary>
		/// <param name="context">Entity Framework Core database context</param>
		/// <param name="initialModel">Initial model, only contains migration history</param>
		protected void MigrateRelationalDatabase(DbContext context, IModel initialModel) {
			var serviceProvider = ((IInfrastructure<IServiceProvider>)context).Instance;
			// Get the last migration model
			var lastModel = initialModel;
			var histories = context.Set<EFCoreMigrationHistory>();
			var lastMigration = histories.OrderByDescending(h => h.Revision).FirstOrDefault();
			if (lastMigration != null) {
				// Remove old snapshot code and assembly
				var tempPath = Path.GetTempPath();
				foreach (var file in Directory.EnumerateFiles(
					tempPath, ModelSnapshotFilePrefix + "*").ToList()) {
					try { File.Delete(file); } catch { }
				}
				// Write snapshot code to temp directory and compile it to assembly
				var assemblyName = ModelSnapshotFilePrefix + DateTime.UtcNow.Ticks;
				var codePath = Path.Combine(tempPath, assemblyName + ".cs");
				var assemblyPath = Path.Combine(tempPath, assemblyName + ".dll");
				var compileService = Application.Ioc.Resolve<ICompilerService>();
				var assemblyLoader = Application.Ioc.Resolve<IAssemblyLoader>();
				File.WriteAllText(codePath, lastMigration.Model);
				compileService.Compile(new[] { codePath }, assemblyName, assemblyPath);
				// Load assembly and create the snapshot instance
				var assembly = assemblyLoader.LoadFile(assemblyPath);
				var snapshot = (ModelSnapshot)Activator.CreateInstance(
					assembly.GetTypes().First(t =>
					typeof(ModelSnapshot).GetTypeInfo().IsAssignableFrom(t)));
				lastModel = snapshot.Model;
			}
			// Compare with the newest model
			var modelDiffer = serviceProvider.GetService<IMigrationsModelDiffer>();
			var sqlGenerator = serviceProvider.GetService<IMigrationsSqlGenerator>();
			var commandExecutor = serviceProvider.GetService<IMigrationCommandExecutor>();
			var operations = modelDiffer.GetDifferences(lastModel, context.Model);
			if (operations.Count <= 0) {
				// There no difference
				return;
			}
			// There some difference, we need perform the migration
			var commands = sqlGenerator.Generate(operations, context.Model);
			var connection = serviceProvider.GetService<IRelationalConnection>();
			// Take a snapshot to the newest model
			var codeHelper = new CSharpHelper();
			var generator = new CSharpMigrationsGenerator(
				codeHelper,
				new CSharpMigrationOperationGenerator(codeHelper),
				new CSharpSnapshotGenerator(codeHelper));
			var modelSnapshot = generator.GenerateSnapshot(
				ModelSnapshotNamespace, context.GetType(),
				ModelSnapshotClassPrefix + DateTime.UtcNow.Ticks, context.Model);
			// Insert the history first, if migration failed, delete it
			var history = new EFCoreMigrationHistory(modelSnapshot);
			histories.Add(history);
			context.SaveChanges();
			try {
				// Execute migration commands
				commandExecutor.ExecuteNonQuery(commands, connection);
			} catch {
				histories.Remove(history);
				context.SaveChanges();
				throw;
			}
		}

		/// <summary>
		/// Create database context<br/>
		/// 创建数据库上下文<br/>
		/// </summary>
		/// <returns></returns>
		public IDatabaseContext CreateContext() {
			return new EFCoreDatabaseContext(Database, ConnectionString);
		}
	}
}
