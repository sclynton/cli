// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using Microsoft.Build.Construction;
using System.Linq;

namespace Microsoft.DotNet.ProjectJsonMigration.Transforms
{
    internal class ItemTransformApplicator : ITransformApplicator
    {
        private readonly ProjectRootElement _projectElementGenerator = ProjectRootElement.Create();

        public void Execute<T, U>(
            T element,
            U destinationElement,
            bool mergeExisting) where T : ProjectElement where U : ProjectElementContainer
        {
            if (typeof(T) != typeof(ProjectItemElement))
            {
                throw new ArgumentException(String.Format(
                    LocalizableStrings.ExpectedElementToBeOfTypeNotTypeError,
                    nameof(ProjectItemElement),
                    typeof(T)));
            }

            if (typeof(U) != typeof(ProjectItemGroupElement))
            {
                throw new ArgumentException(String.Format(
                    LocalizableStrings.ExpectedElementToBeOfTypeNotTypeError,
                    nameof(ProjectItemGroupElement),
                    typeof(U)));
            }

            if (element == null)
            {
                return;
            }

            if (destinationElement == null)
            {
                throw new ArgumentException(LocalizableStrings.NullDestinationElementError);
            }

            var item = element as ProjectItemElement;
            var destinationItemGroup = destinationElement as ProjectItemGroupElement;

            MigrationTrace.Instance.WriteLine(String.Format(
                LocalizableStrings.ItemTransformApplicatorHeader,
                nameof(ItemTransformApplicator),
                item.ItemType,
                item.Condition,
                item.Include,
                item.Exclude,
                item.Update));
            MigrationTrace.Instance.WriteLine(String.Format(
                LocalizableStrings.ItemTransformApplicatorItemGroup,
                nameof(ItemTransformApplicator),
                destinationItemGroup.Condition));

            if (mergeExisting)
            {
                // Don't duplicate items or includes
                item = MergeWithExistingItemsWithSameCondition(item, destinationItemGroup);
                if (item == null)
                {
                    MigrationTrace.Instance.WriteLine(String.Format(
                        LocalizableStrings.ItemTransformAppliatorItemCompletelyMerged,
                        nameof(ItemTransformApplicator)));
                    return;
                }

                // Handle duplicate includes between different conditioned items
                item = MergeWithExistingItemsWithNoCondition(item, destinationItemGroup);
                if (item == null)
                {
                    MigrationTrace.Instance.WriteLine(String.Format(
                        LocalizableStrings.ItemTransformAppliatorItemCompletelyMerged,
                        nameof(ItemTransformApplicator)));
                    return;
                }

                item = MergeWithExistingItemsWithACondition(item, destinationItemGroup);
                if (item == null)
                {
                    MigrationTrace.Instance.WriteLine(String.Format(
                        LocalizableStrings.ItemTransformAppliatorItemCompletelyMerged,
                        nameof(ItemTransformApplicator)));
                    return;
                }
            }

            AddItemToItemGroup(item, destinationItemGroup);
        }

        public void Execute<T, U>(
            IEnumerable<T> elements,
            U destinationElement,
            bool mergeExisting) where T : ProjectElement where U : ProjectElementContainer
        {
            foreach (var element in elements)
            {
                Execute(element, destinationElement, mergeExisting);
            }
        }

        private void AddItemToItemGroup(ProjectItemElement item, ProjectItemGroupElement itemGroup)
        {
            var outputItem = itemGroup.ContainingProject.CreateItemElement("___TEMP___");
            outputItem.CopyFrom(item);

            MigrationTrace.Instance.WriteLine(String.Format(
                LocalizableStrings.ItemTransformApplicatorAddItemHeader,
                nameof(ItemTransformApplicator),
                outputItem.ItemType,
                outputItem.Condition,
                outputItem.Include,
                outputItem.Exclude,
                outputItem.Update));

            itemGroup.AppendChild(outputItem);
            outputItem.AddMetadata(item.Metadata, MigrationTrace.Instance);
        }

        private ProjectItemElement MergeWithExistingItemsWithACondition(
            ProjectItemElement item,
            ProjectItemGroupElement destinationItemGroup)
        {
            // This logic only applies to conditionless items
            if (item.ConditionChain().Any() || destinationItemGroup.ConditionChain().Any())
            {
                return item;
            }

            var existingItemsWithACondition =
                    FindExistingItemsWithACondition(item, destinationItemGroup.ContainingProject, destinationItemGroup);

            MigrationTrace.Instance.WriteLine(String.Format(
                LocalizableStrings.ItemTransformApplicatorMergingItemWithExistingItems,
                nameof(ItemTransformApplicator),
                existingItemsWithACondition.Count()));

            foreach (var existingItem in existingItemsWithACondition)
            {
                if (!string.IsNullOrEmpty(item.Include))
                {
                    MergeOnIncludesWithExistingItemsWithACondition(item, existingItem, destinationItemGroup);
                }

                if (!string.IsNullOrEmpty(item.Update))
                {
                    MergeOnUpdatesWithExistingItemsWithACondition(item, existingItem, destinationItemGroup);
                }
            }

            return item;
        }

        private void MergeOnIncludesWithExistingItemsWithACondition(
            ProjectItemElement item,
            ProjectItemElement existingItem,
            ProjectItemGroupElement destinationItemGroup)
        {
            // If this item is encompassing items in a condition, remove the encompassed includes from the existing item
            var encompassedIncludes = item.GetEncompassedIncludes(existingItem, MigrationTrace.Instance);
            if (encompassedIncludes.Any())
            {
                MigrationTrace.Instance.WriteLine(String.Format(
                    LocalizableStrings.ItemTransformApplicatorEncompassedIncludes,
                    nameof(ItemTransformApplicator),
                    string.Join(", ", encompassedIncludes)));
                existingItem.RemoveIncludes(encompassedIncludes);
            }

            // continue if the existing item is now empty
            if (!existingItem.Includes().Any())
            {
                MigrationTrace.Instance.WriteLine(String.Format(
                    LocalizableStrings.ItemTransformApplicatorRemovingItem,
                    nameof(ItemTransformApplicator),
                    existingItem.ItemType,
                    existingItem.Condition,
                    existingItem.Include,
                    existingItem.Exclude));
                existingItem.Parent.RemoveChild(existingItem);
                return;
            }

            // If we haven't continued, the existing item may have includes
            // that need to be removed before being redefined, to avoid duplicate includes
            // Create or merge with existing remove
            var remainingIntersectedIncludes = existingItem.IntersectIncludes(item);

            if (remainingIntersectedIncludes.Any())
            {
                var existingRemoveItem = destinationItemGroup.Items
                    .Where(i =>
                        string.IsNullOrEmpty(i.Include)
                        && string.IsNullOrEmpty(i.Exclude)
                        && !string.IsNullOrEmpty(i.Remove))
                    .FirstOrDefault();

                if (existingRemoveItem != null)
                {
                    var removes = new HashSet<string>(existingRemoveItem.Remove.Split(';'));
                    foreach (var include in remainingIntersectedIncludes)
                    {
                        removes.Add(include);
                    }
                    existingRemoveItem.Remove = string.Join(";", removes);
                }
                else
                {
                    var clearPreviousItem = _projectElementGenerator.CreateItemElement(item.ItemType);
                    clearPreviousItem.Remove = string.Join(";", remainingIntersectedIncludes);

                    AddItemToItemGroup(clearPreviousItem, existingItem.Parent as ProjectItemGroupElement);
                }
            }
        }

        private void MergeOnUpdatesWithExistingItemsWithACondition(
            ProjectItemElement item,
            ProjectItemElement existingItem,
            ProjectItemGroupElement destinationItemGroup)
        {
            // If this item is encompassing items in a condition, remove the encompassed updates from the existing item
            var encompassedUpdates = item.GetEncompassedUpdates(existingItem, MigrationTrace.Instance);
            if (encompassedUpdates.Any())
            {
                MigrationTrace.Instance.WriteLine(String.Format(
                    LocalizableStrings.ItemTransformApplicatorEncompassedUpdates,
                    nameof(ItemTransformApplicator),
                    string.Join(", ", encompassedUpdates)));
                existingItem.RemoveUpdates(encompassedUpdates);
            }

            // continue if the existing item is now empty
            if (!existingItem.Updates().Any())
            {
                MigrationTrace.Instance.WriteLine(String.Format(
                    LocalizableStrings.ItemTransformApplicatorRemovingItem,
                    nameof(ItemTransformApplicator),
                    existingItem.ItemType,
                    existingItem.Condition,
                    existingItem.Update,
                    existingItem.Exclude));
                existingItem.Parent.RemoveChild(existingItem);
                return;
            }

            // If we haven't continued, the existing item may have updates
            // that need to be removed before being redefined, to avoid duplicate updates
            // Create or merge with existing remove
            var remainingIntersectedUpdates = existingItem.IntersectUpdates(item);

            if (remainingIntersectedUpdates.Any())
            {
                var existingRemoveItem = destinationItemGroup.Items
                    .Where(i =>
                        string.IsNullOrEmpty(i.Update)
                        && string.IsNullOrEmpty(i.Exclude)
                        && !string.IsNullOrEmpty(i.Remove))
                    .FirstOrDefault();

                if (existingRemoveItem != null)
                {
                    var removes = new HashSet<string>(existingRemoveItem.Remove.Split(';'));
                    foreach (var update in remainingIntersectedUpdates)
                    {
                        removes.Add(update);
                    }
                    existingRemoveItem.Remove = string.Join(";", removes);
                }
                else
                {
                    var clearPreviousItem = _projectElementGenerator.CreateItemElement(item.ItemType);
                    clearPreviousItem.Remove = string.Join(";", remainingIntersectedUpdates);

                    AddItemToItemGroup(clearPreviousItem, existingItem.Parent as ProjectItemGroupElement);
                }
            }
        }

        private ProjectItemElement MergeWithExistingItemsWithNoCondition(
            ProjectItemElement item,
            ProjectItemGroupElement destinationItemGroup)
        {
            // This logic only applies to items being placed into a condition
            if (!item.ConditionChain().Any() && !destinationItemGroup.ConditionChain().Any())
            {
                return item;
            }

            var existingItemsWithNoCondition =
                    FindExistingItemsWithNoCondition(item, destinationItemGroup.ContainingProject, destinationItemGroup);

            MigrationTrace.Instance.WriteLine(String.Format(
                LocalizableStrings.ItemTransformApplicatorMergingItemWithExistingItems,
                nameof(ItemTransformApplicator),
                existingItemsWithNoCondition.Count()));

            if (!string.IsNullOrEmpty(item.Include))
            {
                // Handle the item being placed inside of a condition, when it is overlapping with a conditionless item
                // If it is not definining new metadata or excludes, the conditioned item can be merged with the
                // conditionless item
                foreach (var existingItem in existingItemsWithNoCondition)
                {
                    var encompassedIncludes = existingItem.GetEncompassedIncludes(item, MigrationTrace.Instance);
                    if (encompassedIncludes.Any())
                    {
                        MigrationTrace.Instance.WriteLine(String.Format(
                            LocalizableStrings.ItemTransformApplicatorEncompassedIncludes,
                            nameof(ItemTransformApplicator),
                            string.Join(", ", encompassedIncludes)));
                        item.RemoveIncludes(encompassedIncludes);
                        if (!item.Includes().Any())
                        {
                            MigrationTrace.Instance.WriteLine(String.Format(
                                LocalizableStrings.ItemTransformApplicatorIgnoringItem,
                                nameof(ItemTransformApplicator),
                                existingItem.ItemType,
                                existingItem.Condition,
                                existingItem.Include,
                                existingItem.Exclude));
                            return null;
                        }
                    }
                }
            }

            if (!string.IsNullOrEmpty(item.Update))
            {
                // Handle the item being placed inside of a condition, when it is overlapping with a conditionless item
                // If it is not definining new metadata or excludes, the conditioned item can be merged with the
                // conditionless item
                foreach (var existingItem in existingItemsWithNoCondition)
                {
                    var encompassedUpdates = existingItem.GetEncompassedUpdates(item, MigrationTrace.Instance);
                    if (encompassedUpdates.Any())
                    {
                        MigrationTrace.Instance.WriteLine(String.Format(
                            LocalizableStrings.ItemTransformApplicatorEncompassedUpdates,
                            nameof(ItemTransformApplicator),
                            string.Join(", ", encompassedUpdates)));
                        item.RemoveUpdates(encompassedUpdates);
                        if (!item.Updates().Any())
                        {
                            MigrationTrace.Instance.WriteLine(String.Format(
                                LocalizableStrings.ItemTransformApplicatorIgnoringItem,
                                nameof(ItemTransformApplicator),
                                existingItem.ItemType,
                                existingItem.Condition,
                                existingItem.Update,
                                existingItem.Exclude));
                            return null;
                        }
                    }
                }
            }

            // If we haven't returned, and there are existing items with a separate condition, we need to 
            // overwrite with those items inside the destinationItemGroup by using a Remove
            if (existingItemsWithNoCondition.Any())
            {
                // Merge with the first remove if possible
                var existingRemoveItem = destinationItemGroup.Items
                    .Where(i =>
                        string.IsNullOrEmpty(i.Include)
                        && string.IsNullOrEmpty(i.Update)
                        && string.IsNullOrEmpty(i.Exclude)
                        && !string.IsNullOrEmpty(i.Remove))
                    .FirstOrDefault();

                var itemsToRemove = string.IsNullOrEmpty(item.Include) ? item.Update : item.Include;
                if (existingRemoveItem != null)
                {
                    existingRemoveItem.Remove += ";" + itemsToRemove;
                }
                else
                {
                    var clearPreviousItem = _projectElementGenerator.CreateItemElement(item.ItemType);
                    clearPreviousItem.Remove = itemsToRemove;

                    AddItemToItemGroup(clearPreviousItem, destinationItemGroup);
                }
            }

            return item;
        }

        private ProjectItemElement MergeWithExistingItemsWithSameCondition(
            ProjectItemElement item,
            ProjectItemGroupElement destinationItemGroup)
        {
            var existingItemsWithSameCondition = FindExistingItemsWithSameCondition(
                item,
                destinationItemGroup.ContainingProject,
                destinationItemGroup);

            MigrationTrace.Instance.WriteLine(String.Format(
                LocalizableStrings.ItemTransformApplicatorMergingItemWithExistingItemsSameChain,
                nameof(TransformApplicator),
                existingItemsWithSameCondition.Count()));

            foreach (var existingItem in existingItemsWithSameCondition)
            {
                var mergeResult = MergeItems(item, existingItem);
                item = mergeResult.InputItem;

                // Existing Item is null when it's entire set of includes has been merged with the MergeItem
                if (mergeResult.ExistingItem == null)
                {
                    existingItem.Parent.RemoveChild(existingItem);
                }
                
                MigrationTrace.Instance.WriteLine(String.Format(
                    LocalizableStrings.ItemTransformApplicatorAddingMergedItem,
                    nameof(TransformApplicator),
                    mergeResult.MergedItem.ItemType,
                    mergeResult.MergedItem.Condition,
                    mergeResult.MergedItem.Include,
                    mergeResult.MergedItem.Exclude));
                AddItemToItemGroup(mergeResult.MergedItem, destinationItemGroup);
            }

            return item;
        }

        private MergeResult MergeItems(ProjectItemElement item, ProjectItemElement existingItem)
        {
            if (!string.IsNullOrEmpty(item.Include))
            {
                return MergeItemsOnIncludes(item, existingItem);
            }

            if (!string.IsNullOrEmpty(item.Update))
            {
                return MergeItemsOnUpdates(item, existingItem);
            }

            throw new InvalidOperationException(LocalizableStrings.CannotMergeItemsWithoutCommonIncludeError);
        }

        /// <summary>
        /// Merges two items on their common sets of includes.
        /// The output is 3 items, the 2 input items and the merged items. If the common
        /// set of includes spans the entirety of the includes of either of the 2 input
        /// items, that item will be returned as null.
        ///
        /// The 3rd output item, the merged item, will have the Union of the excludes and
        /// metadata from the 2 input items. If any metadata between the 2 input items is different,
        /// this will throw.
        ///
        /// This function will mutate the Include property of the 2 input items, removing the common subset.
        /// </summary>
        private MergeResult MergeItemsOnIncludes(ProjectItemElement item, ProjectItemElement existingItem)
        {
            if (!string.Equals(item.ItemType, existingItem.ItemType, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(LocalizableStrings.CannotMergeItemsOfDifferentTypesError);
            }

            var commonIncludes = item.IntersectIncludes(existingItem).ToList();
            var mergedItem = _projectElementGenerator.AddItem(item.ItemType, string.Join(";", commonIncludes));

            mergedItem.UnionExcludes(existingItem.Excludes());
            mergedItem.UnionExcludes(item.Excludes());

            mergedItem.AddMetadata(MergeMetadata(existingItem.Metadata, item.Metadata), MigrationTrace.Instance);

            item.RemoveIncludes(commonIncludes);
            existingItem.RemoveIncludes(commonIncludes);

            var mergeResult = new MergeResult
            {
                InputItem = string.IsNullOrEmpty(item.Include) ? null : item,
                ExistingItem = string.IsNullOrEmpty(existingItem.Include) ? null : existingItem,
                MergedItem = mergedItem
            };

            return mergeResult;
        }

        private MergeResult MergeItemsOnUpdates(ProjectItemElement item, ProjectItemElement existingItem)
        {
            if (!string.Equals(item.ItemType, existingItem.ItemType, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(LocalizableStrings.CannotMergeItemsOfDifferentTypesError);
            }

            var commonUpdates = item.IntersectUpdates(existingItem).ToList();
            var mergedItem = _projectElementGenerator.AddItem(item.ItemType, "placeholder");
            mergedItem.Include = string.Empty;
            mergedItem.Update = string.Join(";", commonUpdates);

            mergedItem.UnionExcludes(existingItem.Excludes());
            mergedItem.UnionExcludes(item.Excludes());

            mergedItem.AddMetadata(MergeMetadata(existingItem.Metadata, item.Metadata), MigrationTrace.Instance);

            item.RemoveUpdates(commonUpdates);
            existingItem.RemoveUpdates(commonUpdates);

            var mergeResult = new MergeResult
            {
                InputItem = string.IsNullOrEmpty(item.Update) ? null : item,
                ExistingItem = string.IsNullOrEmpty(existingItem.Update) ? null : existingItem,
                MergedItem = mergedItem
            };

            return mergeResult;
        }

        private ICollection<ProjectMetadataElement> MergeMetadata(
            ICollection<ProjectMetadataElement> existingMetadataElements,
            ICollection<ProjectMetadataElement> newMetadataElements)
        {
            var mergedMetadata = new List<ProjectMetadataElement>(existingMetadataElements.Select(m => (ProjectMetadataElement) m.Clone()));

            foreach (var newMetadata in newMetadataElements)
            {
                var existingMetadata = mergedMetadata.FirstOrDefault(m =>
                    m.Name.Equals(newMetadata.Name, StringComparison.OrdinalIgnoreCase));
                if (existingMetadata == null)
                {
                    mergedMetadata.Add((ProjectMetadataElement) newMetadata.Clone());
                }
                else
                {
                    MergeMetadata(existingMetadata, (ProjectMetadataElement) newMetadata.Clone());
                }
            }

            return mergedMetadata;
        }

        public void MergeMetadata(ProjectMetadataElement existingMetadata, ProjectMetadataElement newMetadata)
        {
            if (existingMetadata.Value != newMetadata.Value)
            {
                if (existingMetadata.Name == "CopyToOutputDirectory" ||
                    existingMetadata.Name == "CopyToPublishDirectory")
                {
                    existingMetadata.Value =
                        existingMetadata.Value == "Never" || newMetadata.Value == "Never" ?
                            "Never" :
                            "PreserveNewest";
                }
                else if (existingMetadata.Name == "Pack")
                {
                    existingMetadata.Value =
                        existingMetadata.Value == "false" || newMetadata.Value == "false" ?
                            "false" :
                            "true";
                }
                else
                {
                    existingMetadata.Value = string.Join(";", new [] { existingMetadata.Value, newMetadata.Value });
                }
            }
        }

        private IEnumerable<ProjectItemElement> FindExistingItemsWithSameCondition(
            ProjectItemElement item, 
            ProjectRootElement project,
            ProjectElementContainer destinationContainer)
        {
                return project.Items
                    .Where(i => i.Condition == item.Condition)
                    .Where(i => i.Parent.ConditionChainsAreEquivalent(destinationContainer))
                    .Where(i => i.ItemType == item.ItemType)
                    .Where(i => i.IntersectIncludes(item).Any() ||
                                i.IntersectUpdates(item).Any());
        }

        private IEnumerable<ProjectItemElement> FindExistingItemsWithNoCondition(
            ProjectItemElement item,
            ProjectRootElement project,
            ProjectElementContainer destinationContainer)
        {
            return project.Items
                .Where(i => !i.ConditionChain().Any())
                .Where(i => i.ItemType == item.ItemType)
                .Where(i => i.IntersectIncludes(item).Any() ||
                            i.IntersectUpdates(item).Any());
        }

        private IEnumerable<ProjectItemElement> FindExistingItemsWithACondition(
            ProjectItemElement item,
            ProjectRootElement project,
            ProjectElementContainer destinationContainer)
        {
            return project.Items
                .Where(i => i.ConditionChain().Any())
                .Where(i => i.ItemType == item.ItemType)
                .Where(i => i.IntersectIncludes(item).Any() ||
                            i.IntersectUpdates(item).Any());
        }

        private class MergeResult
        {
            public ProjectItemElement InputItem { get; set; }
            public ProjectItemElement ExistingItem { get; set; }
            public ProjectItemElement MergedItem { get; set; }
        }
    }
}
