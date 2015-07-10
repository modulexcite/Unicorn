﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Rainbow.Filtering;
using Rainbow.Model;
using Rainbow.Storage.Sc;
using Sitecore;
using Sitecore.Data.Fields;
using Sitecore.Data.Items;
using Sitecore.Diagnostics;
using Sitecore.Pipelines.Save;
using Sitecore.Web.UI.Sheer;
using Unicorn.Configuration;
using Unicorn.Data;
using Unicorn.Predicates;

namespace Unicorn
{
	/// <summary>
	/// Provides a saveUI pipeline implementation to prevent unintentionally overwriting a changed serialized item on disk.
	/// 
	/// For example, if user A changes an item, then user B pulls that change from SCM but does not sync it into Sitecore,
	/// then user B changes that item, without this handler user A's changes would be overwritten.
	/// 
	/// This handler verifies that the pre-save state of the item matches the field values present in the serialized item on disk (if it's present).
	/// If they dont match a sheer warning is shown.
	/// </summary>
	/// <remarks>
	/// Note that this solution does NOT save you from every possible conflict situation. Some saves do not run the saveUI pipeline, for example:
	/// - Renaming any item
	/// - Moving items
	/// - Changing template field values (source, type) in the template builder
	/// 
	/// This handler is here to attempt to keep you from shooting yourself in the foot, but you really need to sync Unicorn after every update pulled from SCM.
	/// </remarks>
	public class SerializationConflictProcessor
	{
		private readonly IConfiguration[] _configurations;

		public SerializationConflictProcessor()
			: this(UnicornConfigurationManager.Configurations)
		{
			// TODO: implement this against IItemComparer
		}

		protected SerializationConflictProcessor(IConfiguration[] configurations)
		{
			_configurations = configurations;
		}

		public void Process(SaveArgs args)
		{
			Assert.ArgumentNotNull(args, "args");

			// we had errors, and we got a post-back result of no, don't overwrite
			if (args.Result == "no" || args.Result == "undefined")
			{
				args.SaveAnimation = false;
				args.AbortPipeline();
				return;
			}

			// we had errors, and we got a post-back result of yes, allow overwrite
			if (args.IsPostBack) return;

			string error = GetErrorValue(args);

			// no errors detected, we're good
			if (string.IsNullOrEmpty(error)) return;

			SheerResponse.Confirm(error);
			args.WaitForPostBack();
		}

		private string GetErrorValue(SaveArgs args)
		{
			var results = new Dictionary<Item, IList<FieldDesynchronization>>();

			try
			{
				foreach (var item in args.Items)
				{
					Item existingItem = Client.ContentDatabase.GetItem(item.ID, item.Language, item.Version);

					Assert.IsNotNull(existingItem, "Existing item {0} did not exist! This should never occur.", item.ID);

					var existingSitecoreItem = new SerializableItem(existingItem);

					foreach (var configuration in _configurations)
					{
						// ignore conflicts on items that Unicorn is not managing
						if (!configuration.Resolve<IPredicate>().Includes(existingSitecoreItem).IsIncluded) continue;

						ISerializableItem serializedItem = configuration.Resolve<ITargetDataStore>().GetById(existingSitecoreItem.Id, existingSitecoreItem.DatabaseName);
					
						// not having an existing serialized version means no possibility of conflict here
						if (serializedItem == null) continue;

						var fieldFilter = configuration.Resolve<IFieldFilter>();

						var fieldIssues = GetFieldSyncStatus(existingSitecoreItem, serializedItem, fieldFilter);

						if (fieldIssues.Count == 0) continue;

						results.Add(existingItem, fieldIssues);
					}
					
				}

				// no problems
				if (results.Count == 0) return null;

				var sb = new StringBuilder();
				sb.Append("CRITICAL MESSAGE FROM UNICORN:\n");
				sb.Append("You need to run a Unicorn sync. The following fields did not match the serialized version:\n");

				foreach (var item in results)
				{
					if(results.Count > 1)
						sb.AppendFormat("\n{0}: {1}", item.Key.DisplayName, string.Join(", ", item.Value.Select(x => x.FieldName)));
					else
						sb.AppendFormat("\n{0}", string.Join(", ", item.Value.Select(x => x.FieldName)));
				}

				sb.Append("\n\nDo you want to overwrite anyway?\nTHIS MAY CAUSE LOST WORK.");

				return sb.ToString();
			}
			catch (Exception ex)
			{
				Log.Error("Exception occurred while performing serialization conflict check!", ex, this);
				return "Exception occurred: " + ex.Message; // this will cause a few retries
			}
		}

		private IList<FieldDesynchronization> GetFieldSyncStatus(ISerializableItem item, ISerializableItem serializedItem, IFieldFilter fieldFilter)
		{
			var desyncs = new List<FieldDesynchronization>();

			var serializedVersion = serializedItem.Versions.FirstOrDefault(x => x.VersionNumber == item.Version.Number && x.Language.Name == item.Language.Name);
			
			if (serializedVersion == null)
			{
				desyncs.Add(new FieldDesynchronization("Version"));
				return desyncs;
			}

			item.Fields.ReadAll();

			var serializedSharedFields = serializedItem.SharedFields.ToDictionary(x => x.FieldId);
			var serializedFields = serializedVersion.Fields.ToDictionary(x => x.FieldId);

			foreach (Field field in item.Fields)
			{
				// TODO: compare with evaluator from the config so we respect comparers and such, also move the ignored fields to the ignore list FPs
				if (field.ID == FieldIDs.Revision || 
					field.ID == FieldIDs.Updated || 
					field.ID == FieldIDs.Created || 
					field.ID == FieldIDs.CreatedBy || 
					field.ID == FieldIDs.UpdatedBy ||
					field.Type.Equals("attachment", StringComparison.OrdinalIgnoreCase) ||
					!fieldFilter.Includes(field.ID.Guid)) continue; 
				// we're doing a data comparison here - revision, created (by), updated (by) don't matter
				// skipping these fields allows us to ignore spurious saves the template builder makes to unchanged items being conflicts
			
				// find the field in the serialized item in either versioned or shared fields
				ISerializableFieldValue serializedField;

				if (!serializedFields.TryGetValue(field.ID.Guid, out serializedField))
					serializedSharedFields.TryGetValue(field.ID.Guid, out serializedField);

				// we ignore if the field doesn't exist in the serialized item. This is because if you added a field to a template,
				// that does not immediately re-serialize all items based on that template so it's likely innocuous - we're not overwriting anything.
				if (serializedField == null) continue;

				if (!serializedField.Value.Equals(field.Value, StringComparison.Ordinal))
				{
					desyncs.Add(new FieldDesynchronization(field.Name));
				}
			}

			return desyncs;
		}

		private class FieldDesynchronization
		{
			public FieldDesynchronization(string fieldName)
			{
				FieldName = fieldName;
			}

			public string FieldName { get; private set; }
		}
	}
}
