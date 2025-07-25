﻿using System;
using System.Linq;
using System.Xml;
using APSIM.Core;
using Models.Core.Apsim710File;

namespace Models.Core.ApsimFile
{

    /// <summary>
    /// A collection of methods for manipulating the structure of an .apsimx file.
    /// </summary>
    public static class Structure
    {
        /// <summary>
        /// Adds a model as a child to a parent model. Will throw if not allowed.
        /// </summary>
        /// <param name="modelToAdd">The model to add.</param>
        /// <param name="parent">The parent model to add it to.</param>
        public static IModel Add(IModel modelToAdd, IModel parent)
        {
            if (parent.ReadOnly)
                throw new Exception(string.Format("Unable to modify {0} - it is read-only.", parent.Name));

            if (modelToAdd is Simulations s && s.Children.Count == 1)
                modelToAdd = s.Children[0];

            if(!parent.IsChildAllowable(modelToAdd.GetType()))
                throw new ArgumentException($"A {modelToAdd.GetType().Name} cannot be added to a {parent.GetType().Name}.");

            var parentNode = (parent as Model).Node;
            var childNode = parentNode.AddChild(modelToAdd as INodeModel);

            // Ensure the model name is valid.
            EnsureNameIsUnique(modelToAdd);
            childNode.Rename(modelToAdd.Name); // rename node just in case the model was changed.

            //Do error checking on model if it's a Replacements folder
            Folder.IsModelReplacementsFolder(modelToAdd);

            // If the model is being added at runtime then need to resolve links and events.
            ReconnectLinksAndEvents(modelToAdd);

            Apsim.ClearCaches(modelToAdd);
            return modelToAdd;
        }

        /// <summary>
        /// Reconnect links a events.
        /// </summary>
        /// <param name="modelToAdd"></param>
        public static void ReconnectLinksAndEvents(IModel modelToAdd)
        {
            Simulation parentSimulation = modelToAdd.Parent.FindAncestor<Simulation>();
            if (parentSimulation != null && parentSimulation.IsRunning)
            {
                var links = new Links(parentSimulation.ModelServices);
                links.Resolve(modelToAdd, true, throwOnFail: true);
                var events = new Events(modelToAdd);
                events.ConnectEvents();

                // Publish Commencing event
                events.PublishToModelAndChildren("Commencing", new object[] { modelToAdd.Parent, new EventArgs() });

                // Call StartOfSimulation events
                events.PublishToModelAndChildren("StartOfSimulation", new object[] { modelToAdd.Parent, new EventArgs() });
            }
        }

        /// <summary>Adds a new model (as specified by the string argument) to the specified parent.</summary>
        /// <param name="parent">The parent to add the model to</param>
        /// <param name="st">The string representing the new model</param>
        /// <returns>The newly created model.</returns>
        public static IModel Add(string st, IModel parent)
        {
            // The strategy here is to try and add the string as if it was a APSIM Next Gen.
            // string (json or xml). If that throws an exception then try adding it as if
            // it was an APSIM 7.10 string (xml). If that doesn't work throw 'invalid format' exception.
            IModel modelToAdd = null;
            try
            {
                modelToAdd = FileFormat.ReadFromString<IModel>(st).Model as IModel;
            }
            catch (Exception err)
            {
                if (err.Message.StartsWith("Unknown string encountered"))
                    throw;
            }

            if (modelToAdd == null)
            {
                // Try the string as if it was an APSIM 7.10 xml string.
                var xmlDocument = new XmlDocument();
                xmlDocument.LoadXml("<Simulation>" + st + "</Simulation>");
                var importer = new Importer();
                var rootNode = xmlDocument.DocumentElement as XmlNode;
                var convertedNode = importer.AddComponent(rootNode.ChildNodes[0], ref rootNode);
                rootNode.RemoveAll();
                rootNode.AppendChild(convertedNode);
                var newSimulationModel = FileFormat.ReadFromString<IModel>(rootNode.OuterXml).Model as IModel;
                if (newSimulationModel == null || newSimulationModel.Children.Count == 0)
                    throw new Exception("Cannot add model. Invalid model being added.");
                modelToAdd = newSimulationModel.Children[0];
            }

            // Correctly parent all models.
            modelToAdd = Add(modelToAdd, parent);

            return modelToAdd;
        }

        /// <summary>Move a model from one parent to another.</summary>
        /// <param name="model">The model to move.</param>
        /// <param name="newParent">The new parente for the model.</param>
        public static void Move(IModel model, IModel newParent)
        {
            // Remove old model.
            if (model.Parent.Children.Remove(model as Model))
            {
                // Clear the cache for all models in scope of the model to be moved.
                // The models in scope will be different after the move so we will
                // need to do this again after we move the model.
                Apsim.ClearCaches(model);
                EnsureNameIsUnique(model);
                newParent.Children.Add(model as Model);
                model.Parent = newParent;
                Apsim.ClearCaches(model);
            }
            else
                throw new Exception("Cannot move model " + model.Name);
        }

        /// <summary>
        /// Give the specified model a unique name
        /// </summary>
        /// <param name="modelToCheck">The model to check the name of</param>
        private static void EnsureNameIsUnique(IModel modelToCheck)
        {
            string originalName = modelToCheck.Name;
            string newName = originalName;
            int counter = 0;
            IModel siblingWithSameName = modelToCheck.FindSibling(newName);
            while (siblingWithSameName != null && counter < 10000)
            {
                counter++;
                newName = originalName + counter.ToString();
                siblingWithSameName = modelToCheck.FindSibling(newName);
            }
            Simulations sims = modelToCheck.FindAncestor<Simulations>();
            if (sims != null)
            {
                bool stop = false;
                while (!stop && counter < 10000)
                {
                    bool goodName = true;

                    var variable = modelToCheck.Node.GetObject(modelToCheck.Parent.FullPath + "." + newName, relativeTo: sims);
                    if (variable != null) { //found a potential conflict
                        goodName = false;
                        if (variable.DataType.Name.CompareTo(modelToCheck.GetType().Name) == 0)
                            if (modelToCheck.FindSibling(newName) == null)
                                goodName = true;
                    }

                    if (goodName == false)
                    {
                        counter++;
                        newName = originalName + counter.ToString();
                    }
                    else
                    {
                        stop = true;
                    }
                }
            }
            if (counter == 10000)
            {
                throw new Exception("Cannot create a unique name for model: " + originalName);
            }

            modelToCheck.Name = newName;
        }

        /// <summary>Deletes the specified model.</summary>
        /// <param name="model">The model.</param>
        public static bool Delete(IModel model)
        {
            Apsim.ClearCaches(model);

            Node parentNode = (model.Parent as Model).Node;
            parentNode.RemoveChild(model as Model);

            return true;
        }

        /// <summary>Replace one model with another.</summary>
        /// <param name="modelToReplace">The old model to replace.</param>
        /// <param name="replacement">The new model.</param>
        public static IModel Replace(IModel modelToReplace, IModel replacement)
        {
            Model newModel = Apsim.Clone(replacement) as Model;

            // If a resource model (e.g. maize) is copied into replacements, and its
            // property values changed, these changed values will be overriden with the
            // 'accepted' values from the official maize model when the simulation is
            // run, because the model's resource name is not null. This can be manually
            // rectified by editing the json, but such an intervention shouldn't be
            // necessary.
            newModel.ResourceName = null;
            newModel.Name = modelToReplace.Name;
            newModel.Enabled = modelToReplace.Enabled;

            Node parentNode = (modelToReplace.Parent as Model).Node;
            parentNode.ReplaceChild(modelToReplace as INodeModel, newModel);

/*            int index = modelToReplace.Parent.Children.IndexOf(modelToReplace as Model);
            modelToReplace.Parent.Children[index] = newModel;
            newModel.Parent = modelToReplace.Parent;
            newModel.Name = modelToReplace.Name;
*/

            // Remove existing model from parent.
//            modelToReplace.Parent = null;

            Apsim.ClearCaches(modelToReplace);

/*            // Don't call newModel.Parent.OnCreated(), because if we're replacing
            // a child of a resource model, the resource model's OnCreated event
            // will make it reread the resource string and replace this child with
            // the 'official' child from the resource.
            newModel.OnCreated();
            foreach (var model in newModel.FindAllDescendants().ToList())
                model.OnCreated();
*/
            return newModel;
        }
    }
}
