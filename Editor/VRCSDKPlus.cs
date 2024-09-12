using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using AnimatorController = UnityEditor.Animations.AnimatorController;
using AnimatorControllerParameter = UnityEngine.AnimatorControllerParameter;
using AnimatorControllerParameterType = UnityEngine.AnimatorControllerParameterType;
using Object = UnityEngine.Object;

namespace Editor
{
    internal abstract class VrcsdkPlus
    {
        private static bool _initialized;
        private static GUIContent _redWarnIcon;
        private static GUIContent _yellowWarnIcon;
        private static GUIStyle CenteredLabel => new(GUI.skin.label) {alignment = TextAnchor.MiddleCenter};
        private static readonly string[] AllPlayables =
        {
            "Base",
            "Additive",
            "Gesture",
            "Action",
            "FX",
            "Sitting",
            "TPose",
            "IKPose"
        };
        
        private static VRCAvatarDescriptor _avatar;
        private static VRCAvatarDescriptor[] _validAvatars;
        private static AnimatorControllerParameter[] _validParameters;

        private static string[] _validPlayables;
        private static int[] _validPlayableIndexes;

        private static void InitConstants()
        {
            if (_initialized) return;
            _redWarnIcon = new GUIContent(EditorGUIUtility.IconContent("CollabError"));
            //advancedPopupMethod = typeof(EditorGUI).GetMethod("AdvancedPopup", BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(Rect), typeof(int), typeof(string[]) }, null);
            _yellowWarnIcon = new GUIContent(EditorGUIUtility.IconContent("d_console.warnicon.sml"));
            _initialized = true;
        }

        private static void RefreshAvatar(Func<VRCAvatarDescriptor, bool> favoredAvatar = null)
        {
            RefreshAvatar(ref _avatar, ref _validAvatars, null, favoredAvatar);
            RefreshAvatarInfo();
        }

        private static void RefreshAvatarInfo()
        {
            RefreshValidParameters();
            RefreshValidPlayables();
        }
        private static void RefreshValidParameters()
        {
            if (!_avatar)
            {
                _validParameters = Array.Empty<AnimatorControllerParameter>();
                return;
            }
            var validParams = new List<AnimatorControllerParameter>();
            foreach (var r in _avatar.baseAnimationLayers.Concat(_avatar.specialAnimationLayers).Select(p => p.animatorController).Concat(_avatar.GetComponentsInChildren<Animator>(true).Select(a => a.runtimeAnimatorController)).Distinct())
            {
                if (!r) continue;

                var c = AssetDatabase.LoadAssetAtPath<AnimatorController>(AssetDatabase.GetAssetPath(r));
                if (c) validParams.AddRange(c.parameters);
            }

            _validParameters = validParams.Distinct().OrderBy(p => p.name).ToArray();
        }

        private static void RefreshValidPlayables()
        {
            if (!_avatar)
            {
                _validPlayables = Array.Empty<string>();
                _validPlayableIndexes = Array.Empty<int>();
                return;
            }
            var myPlayables = new List<(string, int)>();
            for (var i = 0; i < AllPlayables.Length; i++)
            {
                var index = i == 0 ? i : i + 1;
                if (_avatar.GetPlayableLayer((VRCAvatarDescriptor.AnimLayerType)index, out var c))
                {
                    myPlayables.Add((AllPlayables[i], index));
                }
            }

            _validPlayables = new string[myPlayables.Count];
            _validPlayableIndexes = new int[myPlayables.Count];
            for (var i = 0; i < myPlayables.Count; i++)
            {
                _validPlayables[i] = myPlayables[i].Item1;
                _validPlayableIndexes[i] = myPlayables[i].Item2;
            }
        }

        internal sealed class VrcParamsPlus : UnityEditor.Editor
        {
            private static int _maxMemoryCost;
            private static int MaxMemoryCost
            {
                get
                {
                    if (_maxMemoryCost != 0) return _maxMemoryCost;
                    try
                    { _maxMemoryCost = (int) typeof(VRCExpressionParameters).GetField("MAX_PARAMETER_COST", BindingFlags.Static | BindingFlags.Public)?.GetValue(null)!; }
                    catch 
                    {
                        Debug.LogError("Failed to dynamically get MAX_PARAMETER_COST. Falling back to 256");
                        _maxMemoryCost = 256;
                    }

                    return _maxMemoryCost;
                }
            }

            private static readonly bool HasSyncingOption = typeof(VRCExpressionParameters.Parameter).GetField("networkSynced") != null;
            private static bool _editorActive = true;
            private static bool _canCleanup;
            private int _currentCost;
            private string _searchValue;

            private SerializedProperty _parameterList;
            private ReorderableList _parametersOrderList;

            private ParameterStatus[] _parameterStatus;

            private static VRCExpressionParameters _mergeParams;
            
            public override void OnInspectorGUI()
            {
                EditorGUI.BeginChangeCheck();
                using (new GUILayout.HorizontalScope("helpbox"))
                    DrawAdvancedAvatarFull(ref _avatar, _validAvatars, RefreshValidParameters, false, false, false, "Active Avatar");

                _canCleanup = false;
                serializedObject.Update();
                HandleParameterEvents();
                _parametersOrderList.DoLayoutList();
                serializedObject.ApplyModifiedProperties();
                
                if (_canCleanup)
                {
                    using (new GUILayout.HorizontalScope("helpbox"))
                    {
                        GUILayout.Label("Cleanup Invalid, Blank, and Duplicate Parameters");
                        if (ClickableButton("Cleanup"))
                        {
                            RefreshValidParameters();
                            _parameterList.IterateArray((i, p) =>
                            {
                                var name = p.FindPropertyRelative("name").stringValue;
                                if (string.IsNullOrEmpty(name))
                                {
                                    GreenLog($"Deleted blank parameter at index {i}");
                                    _parameterList.DeleteArrayElementAtIndex(i);
                                    return false;
                                }

                                if (_avatar && _validParameters.All(p2 => p2.name != name))
                                {
                                    GreenLog($"Deleted invalid parameter {name}");
                                    _parameterList.DeleteArrayElementAtIndex(i);
                                    return false;
                                }

                                _parameterList.IterateArray((j, p2) =>
                                {
                                    if (name != p2.FindPropertyRelative("name").stringValue) return false;
                                    GreenLog($"Deleted duplicate parameter {name}");
                                    _parameterList.DeleteArrayElementAtIndex(j);

                                    return false;
                                }, i);
                                
                                
                                return false;
                            });
                            serializedObject.ApplyModifiedProperties();
                            RefreshValidParameters();
                            GreenLog("Finished Cleanup!");
                        }
                    }
                }

                EditorGUI.BeginChangeCheck();
                using (new GUILayout.HorizontalScope("helpbox"))
                    _mergeParams = (VRCExpressionParameters)EditorGUILayout.ObjectField("Merge Parameters", null, typeof(VRCExpressionParameters), true);
                if (EditorGUI.EndChangeCheck())
                {
                    if (_mergeParams)
                    {
                        if (_mergeParams.parameters != null)
                        {
                            var myParams = (VRCExpressionParameters) target;
                            Undo.RecordObject(myParams, "Merge Parameters");
                            myParams.parameters = myParams.parameters.Concat(_mergeParams.parameters.Select(p => 
                                new VRCExpressionParameters.Parameter
                                {
                                    defaultValue = p.defaultValue,
                                    name = p.name,
                                    networkSynced = p.networkSynced,
                                    valueType = p.valueType
                                })).ToArray();
                            EditorUtility.SetDirty(myParams);
                        }
                        _mergeParams = null;
                    }
                }

                CalculateTotalCost();
                try
                {
                    using (new EditorGUILayout.HorizontalScope("helpbox"))
                    {
                        GUILayout.FlexibleSpace();
                        using (new GUILayout.VerticalScope())
                        {
                            using (new GUILayout.HorizontalScope())
                            {
                                GUILayout.FlexibleSpace();
                                GUILayout.Label("Total Memory");
                                GUILayout.FlexibleSpace();

                            }

                            using (new GUILayout.HorizontalScope())
                            {
                                GUILayout.FlexibleSpace();
                                GUILayout.Label($"{_currentCost} / {MaxMemoryCost}");
                                if (_currentCost > MaxMemoryCost)
                                    GUILayout.Label(_redWarnIcon, GUILayout.Width(20));
                                GUILayout.FlexibleSpace();

                            }
                        }

                        GUILayout.FlexibleSpace();
                    }
                }
                catch
                {
                    // it sucks seeing this again
                }

                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    Link("Made By @Dreadrith ♡", "https://dreadrith.com/links");
                }
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    Link("Refactored by RunaXR", "https://c0dera.in");
                }

                if (EditorGUI.EndChangeCheck()) RefreshAllParameterStatus();
            }

            private void OnEnable()
            {
                InitConstants();
                RefreshAvatar(a => a.expressionParameters == target);

                _parameterList = serializedObject.FindProperty("parameters");
                RefreshParametersOrderList();
                RefreshAllParameterStatus();
            }


            private static float _guitest = 10;
            private void DrawElement(Rect rect, int index, bool active, bool focused)
            {
                if (!(index < _parameterList.arraySize && index >= 0)) return;
                
                var screenRect = GUIUtility.GUIToScreenRect(rect);
                if (screenRect.y > Screen.currentResolution.height || screenRect.y + screenRect.height < 0) return;

                var parameter = _parameterList.GetArrayElementAtIndex(index);
                var name = parameter.FindPropertyRelative("name");
                var valueType = parameter.FindPropertyRelative("valueType");
                var defaultValue = parameter.FindPropertyRelative("defaultValue");
                var saved = parameter.FindPropertyRelative("saved");
                var synced = HasSyncingOption ? parameter.FindPropertyRelative("networkSynced") : null;

                var status = _parameterStatus[index];
                var parameterEmpty = status.ParameterEmpty;
                var parameterAddable = status.ParameterAddable;
                var parameterIsDuplicate = status.ParameterIsDuplicate;
                var hasWarning = status.HasWarning;
                var warnMsg = parameterEmpty ? "Blank Parameter" : parameterIsDuplicate ? "Duplicate Parameter! May cause issues!" : "Parameter not found in any playable controller of Active Avatar";
                var matchedParameter = status.MatchedParameter;

                _canCleanup |= hasWarning;

                #region Rects
                rect.y += 1;
                rect.height = 18;


                Rect UseNext( float width, bool fixedWidth = false, float position = -1, bool fixedPosition = false)
                {
                    var currentRect = rect;
                    currentRect.width = fixedWidth ? width : width * rect.width / 100;
                    currentRect.height = rect.height;
                    currentRect.x = Mathf.Approximately(position, -1) ? rect.x : fixedPosition ? position : rect.x + position * rect.width / 100;
                    currentRect.y = rect.y;
                    rect.x += currentRect.width;
                    return currentRect;
                }

                Rect UseEnd(ref Rect r, float width, bool fixedWidth = false, float positionOffset = -1, bool fixedPosition = false)
                {
                    var returnRect = r;
                    returnRect.width = fixedWidth ? width : width * r.width / 100;
                    var positionAdjust = Mathf.Approximately(positionOffset, -1) ? 0 : fixedPosition ? positionOffset : positionOffset * r.width / 100;
                    returnRect.x = r.x + r.width - returnRect.width - positionAdjust;
                    r.width -= returnRect.width + positionAdjust;
                    return returnRect;
                }
                
                var contextRect = rect;
                contextRect.x -= 20;
                contextRect.width = 20;
                
                var removeRect = UseEnd(ref rect, 32, true, 4, true);
                var syncedRect = HasSyncingOption ? UseEnd(ref rect, 18, true, 16f, true) : Rect.zero;
                var savedRect = UseEnd(ref rect, 18, true, HasSyncingOption ? 34f : 16, true);
                var defaultRect = UseEnd(ref rect, 85, true, 32, true);
                var typeRect = UseEnd(ref rect, 85, true, 12, true);
                var warnRect = UseEnd(ref rect, 18, true, 4, true);
                var addRect = hasWarning && parameterAddable ? UseEnd(ref rect, 55, true, 4, true) : Rect.zero;
                var dropdownRect = UseEnd(ref rect, 21, true, 1, true);
                dropdownRect.x -= 3;
                var nameRect = UseNext(100);

                //Rect removeRect = new Rect(rect.x + rect.width - 36, rect.y, 32, 18);
                //Rect syncedRect = new Rect(rect.x + rect.width - 60, rect.y, 14, 18);
                #endregion

                using (new EditorGUI.DisabledScope(!string.IsNullOrEmpty(_searchValue) && !Regex.IsMatch(name.stringValue, $"(?i){_searchValue}")))
                {
                    //Hacky way to avoid proper UI Layout 
                    var parameterFieldName = $"namefield{index}";
                    
                    using (new EditorGUI.DisabledScope(_validParameters.Length == 0))
                        if (GUI.Button(dropdownRect, GUIContent.none, EditorStyles.popup))
                        {

                            var filteredParameters = _validParameters.Where(conParam => !_parameterList.IterateArray((_, prop) => prop.FindPropertyRelative("name").stringValue == conParam.name)).ToArray();
                            if (filteredParameters.Any())
                            {
                                var textDropdown = new VrcsdkPlusToolbox.CustomDropdown<AnimatorControllerParameter>(null, filteredParameters, item =>
                                {
                                    using (new GUILayout.HorizontalScope())
                                    {
                                        GUILayout.Label(item.Value.name);
                                        GUILayout.Label(item.Value.type.ToString(), VrcsdkPlusToolbox.Styles.Label.TypeLabel, GUILayout.ExpandWidth(false));
                                    }
                                }, (_, conParam) =>
                                {
                                    name.stringValue = conParam.name;
                                    name.serializedObject.ApplyModifiedProperties();
                                    RefreshAllParameterStatus();
                                });
                                textDropdown.EnableSearch((conParameter, search) => Regex.IsMatch(conParameter.name, $@"(?i){search}"));
                                textDropdown.Show(nameRect);
                            }
                        }

                    GUI.SetNextControlName(parameterFieldName);
                    EditorGUI.PropertyField(nameRect, name, GUIContent.none);
                    EditorGUI.PropertyField(typeRect, valueType, GUIContent.none);
                    EditorGUI.PropertyField(savedRect, saved, GUIContent.none);

                    GUI.Label(nameRect, matchedParameter != null ? $"({matchedParameter.type})" : "(?)", VrcsdkPlusToolbox.Styles.Label.RightPlaceHolder);

                    if (HasSyncingOption) EditorGUI.PropertyField(syncedRect, synced, GUIContent.none);

                    if (parameterAddable)
                    {
                        using (var change = new EditorGUI.ChangeCheckScope())
                        {
                            w_MakeRectLinkCursor(addRect);
                            var dummy = EditorGUI.IntPopup(addRect, -1, _validPlayables, _validPlayableIndexes);
                            if (change.changed)
                            {
                                var playable = (VRCAvatarDescriptor.AnimLayerType) dummy;
                                if (_avatar.GetPlayableLayer(playable, out var c))
                                {
                                    if (c.parameters.All(p => p.name != name.stringValue))
                                    {
                                        var paramType = valueType.enumValueIndex switch
                                        {
                                            0 => AnimatorControllerParameterType.Int,
                                            1 => AnimatorControllerParameterType.Float,
                                            _ => AnimatorControllerParameterType.Bool
                                        };

                                        c.AddParameter(new AnimatorControllerParameter()
                                        {
                                            name = name.stringValue,
                                            type = paramType,
                                            defaultFloat = defaultValue.floatValue,
                                            defaultInt = (int) defaultValue.floatValue,
                                            defaultBool = defaultValue.floatValue > 0
                                        });

                                        GreenLog($"Added {paramType} {name.stringValue} to {playable} Playable Controller");
                                    }

                                    RefreshValidParameters();
                                }
                            }
                        }

                        addRect.x += 3;
                        GUI.Label(addRect, "Add");
                    }

                    if (hasWarning) GUI.Label(warnRect, new GUIContent(_yellowWarnIcon) {tooltip = warnMsg});

                    switch (valueType.enumValueIndex)
                    {
                        case 2:
                            EditorGUI.BeginChangeCheck();
                            var dummy = EditorGUI.Popup(defaultRect, defaultValue.floatValue == 0 ? 0 : 1, new[] {"False", "True"});
                            if (EditorGUI.EndChangeCheck())
                                defaultValue.floatValue = dummy;
                            break;
                        default:
                            EditorGUI.PropertyField(defaultRect, defaultValue, GUIContent.none);
                            break;
                    }

                    w_MakeRectLinkCursor(removeRect);
                    if (GUI.Button(removeRect, VrcsdkPlusToolbox.GUIContent.Remove, VrcsdkPlusToolbox.Styles.Label.RemoveIcon))
                        DeleteParameter(index);
                }

                var e = Event.current;
                if (e.type != EventType.ContextClick || !contextRect.Contains(e.mousePosition)) return;
                e.Use();
                var menu = new GenericMenu();
                menu.AddItem(new GUIContent("Duplicate"), false, () => DuplicateParameter(index));
                menu.AddSeparator(string.Empty);
                menu.AddItem(new GUIContent("Delete"), false, () => DeleteParameter(index));
                menu.ShowAsContext();
            }
          

            private void DrawHeader(Rect rect)
            {
                #region Rects
                /*rect.y += 1;
                rect.height = 18;

                Rect baseRect = rect;

                Rect UseNext(float width, bool fixedWidth = false, float position = -1, bool fixedPosition = false)
                {
                    Rect currentRect = baseRect;
                    currentRect.width = fixedWidth ? width : width * baseRect.width / 100;
                    currentRect.height = baseRect.height;
                    currentRect.x = position == -1 ? baseRect.x : fixedPosition ? position : rect.x + position * baseRect.width / 100; ;
                    currentRect.y = baseRect.y;
                    baseRect.x += currentRect.width;
                    return currentRect;
                }

                Rect UseEnd(ref Rect r, float width, bool fixedWidth = false, float positionOffset = -1, bool fixedPosition = false)
                {
                    Rect returnRect = r;
                    returnRect.width = fixedWidth ? width : width * r.width / 100;
                    float positionAdjust = positionOffset == -1 ? 0 : fixedPosition ? positionOffset : positionOffset * r.width / 100;
                    returnRect.x = r.x + r.width - returnRect.width - positionAdjust;
                    r.width -= returnRect.width + positionAdjust;
                    return returnRect;
                }

                UseEnd(ref rect, 32, true, 4, true);
                Rect syncedRect = UseEnd(ref rect, 55, true);
                Rect savedRect = UseEnd(ref rect, 55, true);
                Rect defaultRect = UseEnd(ref rect, 60, true, 30, true);
                Rect typeRect = UseNext(16.66f);
                Rect nameRect = UseNext(rect.width * 0.4f, true);
                Rect searchIconRect = nameRect;
                searchIconRect.x += searchIconRect.width / 2 - 40;
                searchIconRect.width = 18;
                Rect searchRect = Rect.zero;
                Rect searchClearRect = Rect.zero;

                UseNext(canCleanup ? 12 : 26, true);
                UseNext(12, true);*/

                rect.y += 1;
                rect.height = 18;


                Rect UseNext(float width, bool fixedWidth = false, float position = -1, bool fixedPosition = false)
                {
                    var currentRect = rect;
                    currentRect.width = fixedWidth ? width : width * rect.width / 100;
                    currentRect.height = rect.height;
                    currentRect.x = Mathf.Approximately(position, -1) ? rect.x : fixedPosition ? position : rect.x + position * rect.width / 100;
                    currentRect.y = rect.y;
                    rect.x += currentRect.width;
                    return currentRect;
                }

                Rect UseEnd(ref Rect r, float width, bool fixedWidth = false, float positionOffset = -1, bool fixedPosition = false)
                {
                    var returnRect = r;
                    returnRect.width = fixedWidth ? width : width * r.width / 100;
                    var positionAdjust = Mathf.Approximately(positionOffset, -1) ? 0 : fixedPosition ? positionOffset : positionOffset * r.width / 100;
                    returnRect.x = r.x + r.width - returnRect.width - positionAdjust;
                    r.width -= returnRect.width + positionAdjust;
                    return returnRect;
                }

                UseEnd(ref rect, 32, true, 4, true);
                var syncedRect = HasSyncingOption ? UseEnd(ref rect, 54, true) : Rect.zero;
                var savedRect = UseEnd(ref rect, 54, true);
                var defaultRect = UseEnd(ref rect, 117, true);
                var typeRect = UseEnd(ref rect, 75, true);
                UseEnd(ref rect, 48, true);
                var nameRect = UseNext(100);

                //guitest = EditorGUILayout.FloatField(guitest);

                var searchIconRect = nameRect;
                searchIconRect.x += searchIconRect.width / 2 - 40;
                searchIconRect.width = 18;
                var searchRect = Rect.zero;
                var searchClearRect = Rect.zero;
                #endregion
                
                const string controlName = "VRCSDKParameterSearch";
                if (VrcsdkPlusToolbox.HasReceivedCommand(VrcsdkPlusToolbox.EventCommands.Find)) GUI.FocusControl(controlName);
                VrcsdkPlusToolbox.HandleTextFocusConfirmCommands(controlName, onCancel: () => _searchValue = string.Empty);
                var isFocused = GUI.GetNameOfFocusedControl() == controlName;
                var isSearching = isFocused || !string.IsNullOrEmpty(_searchValue);
                if (isSearching)
                {
                    searchRect = nameRect; searchRect.x += 14; searchRect.width -= 14;
                    searchClearRect = searchRect; searchClearRect.x += searchRect.width - 18; searchClearRect.y -= 1; searchClearRect.width = 16;
                }

                w_MakeRectLinkCursor(searchIconRect);
                if (GUI.Button(searchIconRect, VrcsdkPlusToolbox.GUIContent.Search, CenteredLabel))
                    EditorGUI.FocusTextInControl(controlName);
                
                GUI.Label(nameRect, new GUIContent("Name","Name of the Parameter. This must match the name of the parameter that it is controlling in the playable layers. Case sensitive."), CenteredLabel);


                w_MakeRectLinkCursor(searchClearRect);
                if (GUI.Button(searchClearRect, string.Empty, GUIStyle.none))
                {
                    _searchValue = string.Empty;
                    if (isFocused) GUI.FocusControl(string.Empty);
                }
                GUI.SetNextControlName(controlName);
                _searchValue = GUI.TextField(searchRect, _searchValue, "SearchTextField");
                GUI.Button(searchClearRect, VrcsdkPlusToolbox.GUIContent.Clear, CenteredLabel);
                GUI.Label(typeRect, new GUIContent("Type", "Type of the Parameter."), CenteredLabel);
                GUI.Label(defaultRect, new GUIContent("Default", "The default/start value of this parameter."), CenteredLabel);
                GUI.Label(savedRect, new GUIContent("Saved","Value will stay when loading avatar or changing worlds"), CenteredLabel);
               
                if (HasSyncingOption) 
                    GUI.Label(syncedRect, new GUIContent("Synced", "Value will be sent over the network to remote users. This is needed if this value should be the same locally and remotely. Synced parameters count towards the total memory usage."), CenteredLabel);

            }

            private void HandleParameterEvents()
            {
                if (!_parametersOrderList.HasKeyboardControl()) return;
                if (!_parametersOrderList.TryGetActiveIndex(out var index)) return;
                if (VrcsdkPlusToolbox.HasReceivedCommand(VrcsdkPlusToolbox.EventCommands.Duplicate))
                    DuplicateParameter(index);
                
                if (VrcsdkPlusToolbox.HasReceivedAnyDelete())
                    DeleteParameter(index);
            }

            
            #region Automated Methods
            [MenuItem("CONTEXT/VRCExpressionParameters/[SDK+] Toggle Editor", false, 899)]
            private static void ToggleEditor()
            {
                _editorActive = !_editorActive;

                var targetType = ExtendedGetType("VRCExpressionParameters");
                if (targetType == null)
                {
                    Debug.LogError("[VRCSDK+] VRCExpressionParameters was not found! Could not apply custom editor.");
                    return;
                }
                if (_editorActive) OverrideEditor(targetType, typeof(VrcParamsPlus));
                else
                {
                    var expressionsEditor = ExtendedGetType("VRCExpressionParametersEditor");
                    if (expressionsEditor == null)
                    {
                        Debug.LogWarning("[VRCSDK+] VRCExpressionParametersEditor was not found! Could not apply custom editor");
                        return;
                    }
                    OverrideEditor(targetType, expressionsEditor);
                }

            }

            private void RefreshAllParameterStatus()
            {
                var expressionParameters = (VRCExpressionParameters)target;
                if (expressionParameters.parameters == null)
                {
                    expressionParameters.parameters = Array.Empty<VRCExpressionParameters.Parameter>();
                    EditorUtility.SetDirty(expressionParameters);
                }
                var parameters = expressionParameters.parameters;
                _parameterStatus = new ParameterStatus[parameters.Length];

                for (var index = 0; index < parameters.Length; index++)
                {
                    var exParameter = expressionParameters.parameters[index];
                    var matchedParameter = _validParameters.FirstOrDefault(conParam => conParam.name == exParameter.name);
                    var parameterEmpty = string.IsNullOrEmpty(exParameter.name);
                    var parameterIsValid = matchedParameter != null;
                    var parameterAddable = _avatar && !parameterIsValid && !parameterEmpty;
                    var parameterIsDuplicate = !parameterEmpty && expressionParameters.parameters.Where((p2, i) => index != i && exParameter.name == p2.name).Any(); ;
                    var hasWarning = (_avatar && !parameterIsValid) || parameterEmpty || parameterIsDuplicate;
                    _parameterStatus[index] = new ParameterStatus()
                    {
                        ParameterEmpty = parameterEmpty,
                        ParameterAddable = parameterAddable,
                        ParameterIsDuplicate = parameterIsDuplicate,
                        HasWarning = hasWarning,
                        MatchedParameter = matchedParameter
                    };
                }
            }

            private void CalculateTotalCost()
            {
                _currentCost = 0;
                for (var i = 0; i < _parameterList.arraySize; i++)
                {
                    var p = _parameterList.GetArrayElementAtIndex(i);
                    var synced = p.FindPropertyRelative("networkSynced");
                    if (synced != null && !synced.boolValue) continue;
                    _currentCost += p.FindPropertyRelative("valueType").enumValueIndex == 2 ? 1 : 8;
                }
            }

            private void RefreshParametersOrderList()
            {
                _parametersOrderList = new ReorderableList(serializedObject, _parameterList, true, true, true, false)
                {
                    drawElementCallback = DrawElement,
                    drawHeaderCallback = DrawHeader
                };
                _parametersOrderList.onReorderCallback += _ => RefreshAllParameterStatus();
                _parametersOrderList.onAddCallback = _ =>
                {
                    _parameterList.InsertArrayElementAtIndex(_parameterList.arraySize);
                    MakeParameterUnique(_parameterList.arraySize - 1);
                };
            }

            private void DuplicateParameter(int index)
            {
                _parameterList.InsertArrayElementAtIndex(index);
                MakeParameterUnique(index+1);
                _parameterList.serializedObject.ApplyModifiedProperties();
                RefreshAllParameterStatus();
            }

            private void DeleteParameter(int index)
            {
                _parameterList.DeleteArrayElementAtIndex(index);
                _parameterList.serializedObject.ApplyModifiedProperties();
                RefreshAllParameterStatus();
            }
            private void MakeParameterUnique(int index)
            {
                var newElement = _parameterList.GetArrayElementAtIndex(index);
                var nameProp = newElement.FindPropertyRelative("name");
                nameProp.stringValue = VrcsdkPlusToolbox.GenerateUniqueString(nameProp.stringValue, newName =>
                {
                    for (var i = 0; i < _parameterList.arraySize; i++)
                    {
                        if (i == index) continue;
                        var p = _parameterList.GetArrayElementAtIndex(i);
                        if (p.FindPropertyRelative("name").stringValue == newName) return false;
                    }
                    return true;
                });
            }

            #endregion

            private struct ParameterStatus
            {
                internal bool ParameterEmpty;
                internal bool ParameterAddable;
                internal bool ParameterIsDuplicate;
                internal bool HasWarning;
                internal AnimatorControllerParameter MatchedParameter;
            }

        }

        internal sealed class VrcMenuPlus : UnityEditor.Editor, IHasCustomMenu
        {
            private static bool _editorActive = true;
            private static VRCAvatarDescriptor _avatar;
            private VRCAvatarDescriptor[] _validAvatars;
            private ReorderableList _controlsList;

            private static readonly LinkedList<VRCExpressionsMenu> MenuHistory = new();
            private static LinkedListNode<VRCExpressionsMenu> _currentNode;
            private static VRCExpressionsMenu _lastMenu;

            private static VRCExpressionsMenu _moveSourceMenu;
            private static VRCExpressionsMenu.Control _moveTargetControl;
            private static bool _isMoving;

            #region Initialization
            private void ReInitializeAll()
            {
                CheckAvatar();
                CheckMenu();
                InitializeList();
            }

            private void CheckAvatar()
            {
                _validAvatars = FindObjectsOfType<VRCAvatarDescriptor>();
                if (_validAvatars.Length == 0) _avatar = null;
                else if (!_avatar) _avatar = _validAvatars[0];
            }

            private void CheckMenu()
            {
                var currentMenu = target as VRCExpressionsMenu;
                if (!currentMenu || currentMenu == _lastMenu) return;

                if (_currentNode != null && MenuHistory.Last != _currentNode)
                {
                    var node = _currentNode.Next;
                    while (node != null)
                    {
                        var nextNode = node.Next;
                        MenuHistory.Remove(node);
                        node = nextNode;
                    }
                }

                _lastMenu = currentMenu;
                _currentNode = MenuHistory.AddLast(currentMenu);
            }

            private void InitializeList()
            {
                var l = serializedObject.FindProperty("controls");
                _controlsList = new ReorderableList(serializedObject, l, true, true, true, false);
                _controlsList.onCanAddCallback += reorderableList => reorderableList.count < 8;
                _controlsList.onAddCallback = _ =>
                {
                    var controlsProp = _controlsList.serializedProperty;
                    var index = controlsProp.arraySize++;
                    _controlsList.index = index;

                    var c = controlsProp.GetArrayElementAtIndex(index);
                    c.FindPropertyRelative("name").stringValue = "New Control";
                    c.FindPropertyRelative("icon").objectReferenceValue = null;
                    c.FindPropertyRelative("parameter").FindPropertyRelative("name").stringValue = "";
                    c.FindPropertyRelative("type").enumValueIndex = 1;
                    c.FindPropertyRelative("subMenu").objectReferenceValue = null;
                    c.FindPropertyRelative("labels").ClearArray();
                    c.FindPropertyRelative("subParameters").ClearArray();
                    c.FindPropertyRelative("value").floatValue = 1;
                };
                _controlsList.drawHeaderCallback = rect =>
                {
                    if (_isMoving && Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
                    {
                        _isMoving = false;
                        Repaint();
                    }
                    EditorGUI.LabelField(rect, $"Controls ({_controlsList.count} / 8)");

                    // Draw copy, paste, duplicate, and move buttons
                    #region Rects
                    var copyRect = new Rect(
                        rect.x + rect.width - rect.height - ((rect.height + VrcsdkPlusToolbox.Styles.Padding) * 3),
                        rect.y,
                        rect.height,
                        rect.height);

                    var pasteRect = new Rect(
                        copyRect.x + copyRect.width + VrcsdkPlusToolbox.Styles.Padding,
                        copyRect.y,
                        copyRect.height,
                        copyRect.height);

                    var duplicateRect = new Rect(
                        pasteRect.x + pasteRect.width + VrcsdkPlusToolbox.Styles.Padding,
                        pasteRect.y,
                        pasteRect.height,
                        pasteRect.height);

                    var moveRect = new Rect(
                        duplicateRect.x + duplicateRect.width + VrcsdkPlusToolbox.Styles.Padding,
                        duplicateRect.y,
                        duplicateRect.height,
                        duplicateRect.height);
                    
                    #endregion

                    var isFull = _controlsList.count >= 8;
                    var isEmpty = _controlsList.count == 0;
                    var hasIndex = _controlsList.TryGetActiveIndex(out var index);
                    var hasFocus = _controlsList.HasKeyboardControl();
                    if (!hasIndex) index = _controlsList.count;
                    using (new EditorGUI.DisabledScope(isEmpty || !hasFocus || !hasIndex))
                    {
                        #region Copy

                        w_MakeRectLinkCursor(copyRect);
                        if (GUI.Button(copyRect, VrcsdkPlusToolbox.GUIContent.Copy, GUI.skin.label))
                            CopyControl(index);
                                

                        #endregion

                        // This section was also created entirely by GitHub Copilot :3

                        #region Duplicate

                        using (new EditorGUI.DisabledScope(isFull))
                        {
                            w_MakeRectLinkCursor(duplicateRect);
                            if (GUI.Button(duplicateRect, isFull ? new GUIContent(VrcsdkPlusToolbox.GUIContent.Duplicate) { tooltip = VrcsdkPlusToolbox.GUIContent.MenuFullTooltip } : VrcsdkPlusToolbox.GUIContent.Duplicate, GUI.skin.label))
                                DuplicateControl(index);
                        }

                        #endregion
                    }

                    #region Paste
                    using (new EditorGUI.DisabledScope(!CanPasteControl()))
                    {
                        w_MakeRectLinkCursor(pasteRect);
                        if (GUI.Button(pasteRect, VrcsdkPlusToolbox.GUIContent.Paste, GUI.skin.label))
                        {
                            var menu = new GenericMenu();
                            menu.AddItem(new GUIContent("Paste values"), false, isEmpty || !hasFocus ? null : () => PasteControl(index, false));
                            menu.AddItem(
                                new GUIContent("Insert as new"),
                                false,
                                isFull ? null : () => PasteControl(index, true)
                            );
                            menu.ShowAsContext();
                        }
                    }
                    #endregion


                    #region Move
                    using (new EditorGUI.DisabledScope((_isMoving && isFull) || (!_isMoving && (!hasFocus || isEmpty))))
                    {
                        w_MakeRectLinkCursor(moveRect);
                        if (!GUI.Button(moveRect,
                                _isMoving
                                    ? isFull
                                        ? new GUIContent(VrcsdkPlusToolbox.GUIContent.Place)
                                            { tooltip = VrcsdkPlusToolbox.GUIContent.MenuFullTooltip }
                                        : VrcsdkPlusToolbox.GUIContent.Place
                                    : VrcsdkPlusToolbox.GUIContent.Move, GUI.skin.label)) return;
                        if (!_isMoving) MoveControl(index);
                        else PlaceControl(index);
                    }

                    #endregion


                };
                _controlsList.drawElementCallback = (rect2, index, _, focused) =>
                {
                    if (!(index < l.arraySize && index >= 0)) return;
                    var controlProp = l.GetArrayElementAtIndex(index);
                    var controlType = controlProp.FindPropertyRelative("type").ToControlType();
                    var removeRect = new Rect(rect2.width + 3, rect2.y + 1, 32, 18);
                    rect2.width -= 48;
                    // Draw control type
                    EditorGUI.LabelField(rect2, controlType.ToString(), focused
                            ? VrcsdkPlusToolbox.Styles.Label.TypeFocused
                            : VrcsdkPlusToolbox.Styles.Label.Type);

                    // Draw control name
                    var nameGuiContent = new GUIContent(controlProp.FindPropertyRelative("name").stringValue);
                    var emptyName = string.IsNullOrEmpty(nameGuiContent.text);
                    if (emptyName) nameGuiContent.text = "[Unnamed]";

                    var nameRect = new Rect(rect2.x, rect2.y, VrcsdkPlusToolbox.Styles.Label.RichText.CalcSize(nameGuiContent).x, rect2.height);

                    EditorGUI.LabelField(nameRect,
                        new GUIContent(nameGuiContent),
                        emptyName ? VrcsdkPlusToolbox.Styles.Label.PlaceHolder : VrcsdkPlusToolbox.Styles.Label.RichText);

                    w_MakeRectLinkCursor(removeRect);
                    if (GUI.Button(removeRect, VrcsdkPlusToolbox.GUIContent.Remove, VrcsdkPlusToolbox.Styles.Label.RemoveIcon))
                        DeleteControl(index);

                    var e = Event.current;
                    
                    if (controlType == VRCExpressionsMenu.Control.ControlType.SubMenu)
                    {
                        if (e.clickCount == 2 && e.type == EventType.MouseDown && rect2.Contains(e.mousePosition))
                        {
                            var sm = controlProp.FindPropertyRelative("subMenu").objectReferenceValue;
                            if (sm) Selection.activeObject = sm;
                            e.Use();
                        }
                    }

                    if (e.type != EventType.ContextClick || !rect2.Contains(e.mousePosition)) return;
                    e.Use();
                    var menu = new GenericMenu();
                    menu.AddItem(new GUIContent("Cut"), false, () => MoveControl(index));
                    menu.AddItem(new GUIContent("Copy"), false, () => CopyControl(index));
                    if (!CanPasteControl()) menu.AddDisabledItem(new GUIContent("Paste"));
                    else
                    {
                        menu.AddItem(new GUIContent("Paste/Values"), false, () =>  PasteControl(index, false));
                        menu.AddItem(new GUIContent("Paste/As New"), false, () =>  PasteControl(index, true));
                    }
                    menu.AddSeparator(string.Empty);
                    menu.AddItem(new GUIContent("Duplicate"), false, () => DuplicateControl(index));
                    menu.AddItem(new GUIContent("Delete"), false, () => DeleteControl(index));
                    menu.ShowAsContext();

                };
            }

            private VRCExpressionParameters.Parameter FetchParameter(string name)
            {
                if (!_avatar || !_avatar.expressionParameters) return null;
                var par = _avatar.expressionParameters;
                return par.parameters?.FirstOrDefault(p => p.name == name);
            }
            #endregion

            public override void OnInspectorGUI()
            {
                serializedObject.Update();
                HandleControlEvents();
                DrawHistory();
                DrawHead();
                DrawBody();
                DrawFooter();
                serializedObject.ApplyModifiedProperties();

            }

            private void OnEnable() => ReInitializeAll();

            private static void DrawHistory()
            {
                using (new GUILayout.HorizontalScope("helpbox"))
                {
                    void CheckHistory()
                    {
                        for (var node = MenuHistory.First; node != null;)
                        {
                            var next = node.Next;
                            if (node.Value == null) MenuHistory.Remove(node);
                            node = next;
                        }
                    }

                    void SetCurrentNode(LinkedListNode<VRCExpressionsMenu> node)
                    {
                        if (node.Value == null) return;
                        _currentNode = node;
                        Selection.activeObject = _lastMenu = _currentNode.Value;
                    }

                    using (new EditorGUI.DisabledScope(_currentNode.Previous == null))
                    {
                        using (new EditorGUI.DisabledScope(_currentNode.Previous == null))
                        {
                            if (ClickableButton("<<", GUILayout.ExpandWidth(false)))
                            {
                                CheckHistory();
                                SetCurrentNode(MenuHistory.First);
                            }

                            if (ClickableButton("<", GUILayout.ExpandWidth(false)))
                            {
                                CheckHistory();
                                SetCurrentNode(_currentNode.Previous);
                            }
                        }
                    }

                    if (ClickableButton(_lastMenu.name, VrcsdkPlusToolbox.Styles.Label.Centered, GUILayout.ExpandWidth(true)))
                        EditorGUIUtility.PingObject(_lastMenu);

                    using (new EditorGUI.DisabledScope(_currentNode.Next == null))
                    {
                        if (ClickableButton(">", GUILayout.ExpandWidth(false)))
                        {
                            CheckHistory();
                            SetCurrentNode(_currentNode.Next);
                        }

                        if (!ClickableButton(">>", GUILayout.ExpandWidth(false))) return;
                        CheckHistory();
                        SetCurrentNode(MenuHistory.Last);
                    }

                }
            }
            private void DrawHead()
            {
                #region Avatar Selector

                // Generate name string array
                var targetsAsString = _validAvatars.Select(t => t.gameObject.name).ToArray();

                // Draw selection
                using (new EditorGUI.DisabledScope(_validAvatars.Length <= 1))
                {
                    using (new VrcsdkPlusToolbox.Container.Horizontal())
                    {
                        var content = new GUIContent("Active Avatar", "The auto-fill and warnings will be based on this avatar's expression parameters");
                        if (_validAvatars.Length >= 1)
                        {
                            using var change = new EditorGUI.ChangeCheckScope();
                            var targetIndex = EditorGUILayout.Popup(
                                content,
                                _validAvatars.FindIndex(_avatar),
                                targetsAsString);

                            if (targetIndex == -1)
                                ReInitializeAll();
                            else if (change.changed)
                            {
                                _avatar = _validAvatars[targetIndex];
                                ReInitializeAll();
                            }
                        }
                        else EditorGUILayout.LabelField(content, new GUIContent("No Avatar Descriptors found"), VrcsdkPlusToolbox.Styles.Label.LabelDropdown);

                        if (_avatar == null || !_avatar.expressionParameters)
                            GUILayout.Label(new GUIContent(VrcsdkPlusToolbox.GUIContent.Error) { tooltip = VrcsdkPlusToolbox.GUIContent.MissingParametersTooltip }, GUILayout.Width(18));
                    }
                }

                #endregion
            }

            private void DrawBody()
            {

                if (_controlsList == null)
                    InitializeList();

                if (_controlsList!.index == -1 && _controlsList.count != 0)
                    _controlsList.index = 0;
                
                _controlsList.DoLayoutList();
                if (_controlsList.count == 0)
                    _controlsList.index = -1;

                // EditorGUILayout.Separator();

                var control = _controlsList.index < 0 || _controlsList.index >= _controlsList.count ? null : _controlsList.serializedProperty.GetArrayElementAtIndex(_controlsList.index);
                var expressionParameters = _avatar == null ? null : _avatar.expressionParameters;

                if (VrcsdkPlusToolbox.Preferences.CompactMode)
                    ControlRenderer.DrawControlCompact(control, expressionParameters);
                else
                    ControlRenderer.DrawControl(control, expressionParameters);

            }

            private static void DrawFooter()
            {
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    GUILayout.Label("Editor by", VrcsdkPlusToolbox.Styles.Label.Watermark);
                    Link("@fox_score","https://github.com/foxscore");
                    GUILayout.Label("&", VrcsdkPlusToolbox.Styles.Label.Watermark);
                    Link("@Dreadrith", "https://dreadrith.com/links");
                }
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    GUILayout.Label("&", VrcsdkPlusToolbox.Styles.Label.Watermark);
                    Link("@RunaXR", "https://c0dera.in");
                }
            }

            private void HandleControlEvents()
            {
                if (!_controlsList.HasKeyboardControl()) return;
                if (!_controlsList.TryGetActiveIndex(out var index)) return;
                var fullMenu = _controlsList.count >= 8;

                if (VrcsdkPlusToolbox.HasReceivedAnyDelete())
                    DeleteControl(index);
                
                if (VrcsdkPlusToolbox.HasReceivedCommand(VrcsdkPlusToolbox.EventCommands.Duplicate))
                    if (!WarnIfFull()) DuplicateControl(index);
                
                if (VrcsdkPlusToolbox.HasReceivedCommand(VrcsdkPlusToolbox.EventCommands.Copy))
                    CopyControl(index);
                
                if (VrcsdkPlusToolbox.HasReceivedCommand(VrcsdkPlusToolbox.EventCommands.Cut))
                    MoveControl(index);

                if (!VrcsdkPlusToolbox.HasReceivedCommand(VrcsdkPlusToolbox.EventCommands.Paste)) return;
                if (_isMoving && !WarnIfFull()) PlaceControl(index);
                else if (CanPasteControl() && !WarnIfFull()) PasteControl(index, true);
                return;

                bool WarnIfFull()
                {
                    if (!fullMenu) return false;
                    Debug.LogWarning(VrcsdkPlusToolbox.GUIContent.MenuFullTooltip);
                    return true;

                }
            }
            
            #region Control Methods
            private void CopyControl(int index)
            {
                EditorGUIUtility.systemCopyBuffer =
                    VrcsdkPlusToolbox.Strings.ClipboardPrefixControl +
                    JsonUtility.ToJson(((VRCExpressionsMenu)target).controls[index]);
            }
            
            private static bool CanPasteControl() => EditorGUIUtility.systemCopyBuffer.StartsWith(VrcsdkPlusToolbox.Strings.ClipboardPrefixControl);
            private void PasteControl(int index, bool asNew)
            {
                if (!CanPasteControl()) return;
                if (!asNew)
                {
                    var control = JsonUtility.FromJson<VRCExpressionsMenu.Control>(
                        EditorGUIUtility.systemCopyBuffer.Substring(VrcsdkPlusToolbox.Strings.ClipboardPrefixControl.Length));

                    Undo.RecordObject(target, "Paste control values");
                    _lastMenu.controls[index] = control;
                }
                else
                {
                    var newControl = JsonUtility.FromJson<VRCExpressionsMenu.Control>(
                        EditorGUIUtility.systemCopyBuffer.Substring(VrcsdkPlusToolbox.Strings.ClipboardPrefixControl.Length));

                    Undo.RecordObject(target, "Insert control as new");
                    if (_lastMenu.controls.Count <= 0)
                    {
                        _lastMenu.controls.Add(newControl);
                        _controlsList.index = 0;
                    }
                    else
                    {
                        var insertIndex = index + 1;
                        if (insertIndex < 0) insertIndex = 0;
                        _lastMenu.controls.Insert(insertIndex, newControl);
                        _controlsList.index = insertIndex;
                    }
                }

                EditorUtility.SetDirty(_lastMenu);
            }

            private void DuplicateControl(int index)
            {
                var controlsProp = _controlsList.serializedProperty;
                controlsProp.InsertArrayElementAtIndex(index);
                _controlsList.index = index + 1;

                var newElement = controlsProp.GetArrayElementAtIndex(index+1);
                var lastName = newElement.FindPropertyRelative("name").stringValue;
                newElement.FindPropertyRelative("name").stringValue = VrcsdkPlusToolbox.GenerateUniqueString(lastName, newName => newName != lastName, false);

                if (Event.current.shift) return;
                var menuParameter = newElement.FindPropertyRelative("parameter");
                if (menuParameter == null) return;
                var parName = menuParameter.FindPropertyRelative("name").stringValue;
                if (string.IsNullOrEmpty(parName)) return;
                var matchedParameter = FetchParameter(parName);
                if (matchedParameter == null) return;
                var controlType = newElement.FindPropertyRelative("type").ToControlType();
                if (controlType != VRCExpressionsMenu.Control.ControlType.Button && controlType != VRCExpressionsMenu.Control.ControlType.Toggle) return;

                if (matchedParameter.valueType == VRCExpressionParameters.ValueType.Bool)
                {
                    menuParameter.FindPropertyRelative("name").stringValue = VrcsdkPlusToolbox.GenerateUniqueString(parName, s => s != parName, false);
                }
                else
                {
                    var controlValueProp = newElement.FindPropertyRelative("value");
                    if (Mathf.Approximately(Mathf.RoundToInt(controlValueProp.floatValue), controlValueProp.floatValue))
                        controlValueProp.floatValue++;
                }
            }

            private void DeleteControl(int index)
            {
                if (_controlsList.index == index) _controlsList.index--;
                _controlsList.serializedProperty.DeleteArrayElementAtIndex(index);
            }

            private static void MoveControl(int index)
            {
                _isMoving = true;
                _moveSourceMenu = _lastMenu;
                _moveTargetControl = _lastMenu.controls[index];
            }

            private void PlaceControl(int index)
            {
                _isMoving = false;
                if (!_moveSourceMenu || _moveTargetControl == null) return;
                Undo.RecordObject(target, "Move control");
                Undo.RecordObject(_moveSourceMenu, "Move control");

                if (_lastMenu.controls.Count <= 0)
                    _lastMenu.controls.Add(_moveTargetControl);
                else 
                {
                    var insertIndex = index + 1;
                    if (insertIndex < 0) insertIndex = 0;
                    _lastMenu.controls.Insert(insertIndex, _moveTargetControl);
                    _moveSourceMenu.controls.Remove(_moveTargetControl);
                }

                EditorUtility.SetDirty(_moveSourceMenu);
                EditorUtility.SetDirty(target);

                if (Event.current.shift) Selection.activeObject = _moveSourceMenu;
            }

            #endregion

            public void AddItemsToMenu(GenericMenu menu) => menu.AddItem(new GUIContent("Compact Mode"), VrcsdkPlusToolbox.Preferences.CompactMode, ToggleCompactMode);
            private static void ToggleCompactMode() => VrcsdkPlusToolbox.Preferences.CompactMode = !VrcsdkPlusToolbox.Preferences.CompactMode;

            [MenuItem("CONTEXT/VRCExpressionsMenu/[SDK+] Toggle Editor", false, 899)]
            private static void ToggleEditor()
            {
                _editorActive = !_editorActive;
                var targetType = ExtendedGetType("VRCExpressionsMenu");
                if (targetType == null)
                {
                    Debug.LogError("[VRCSDK+] VRCExpressionsMenu was not found! Could not apply custom editor.");
                    return;
                }
                if (_editorActive) OverrideEditor(targetType, typeof(VrcMenuPlus));
                else
                {
                    var menuEditor = ExtendedGetType("VRCExpressionsMenuEditor");
                    if (menuEditor == null)
                    {
                        Debug.LogWarning("[VRCSDK+] VRCExpressionsMenuEditor was not found! Could not apply custom editor.");
                        return;
                    }
                    OverrideEditor(targetType, menuEditor);
                }
                //else OverrideEditor(typeof(VRCExpressionsMenu), Type.GetType("VRCExpressionsMenuEditor, Assembly-CSharp-Editor, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null"));
            }

            private static class ControlRenderer
            {
                private const float IconSize = 96;
                private const float IconSpace = IconSize + 3;

                private const float CompactIconSize = 60;
                private const float CompactIconSpace = CompactIconSize + 3;

                public static void DrawControl(SerializedProperty property, VRCExpressionParameters parameters)
                {
                    MainContainer(property);
                    EditorGUILayout.Separator();
                    ParameterContainer(property, parameters);

                    if (property == null) return;
                    EditorGUILayout.Separator();

                    switch ((VRCExpressionsMenu.Control.ControlType)property.FindPropertyRelative("type").intValue)
                    {
                        case VRCExpressionsMenu.Control.ControlType.RadialPuppet:
                            RadialContainer(property, parameters);
                            break;
                        case VRCExpressionsMenu.Control.ControlType.SubMenu:
                            SubMenuContainer(property);
                            break;
                        case VRCExpressionsMenu.Control.ControlType.TwoAxisPuppet:
                            TwoAxisParametersContainer(property, parameters);
                            EditorGUILayout.Separator();
                            AxisCustomisationContainer(property);
                            break;
                        case VRCExpressionsMenu.Control.ControlType.FourAxisPuppet:
                            FourAxisParametersContainer(property, parameters);
                            EditorGUILayout.Separator();
                            AxisCustomisationContainer(property);
                            break;
                        case VRCExpressionsMenu.Control.ControlType.Button:
                            break;
                        case VRCExpressionsMenu.Control.ControlType.Toggle:
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }

                public static void DrawControlCompact(SerializedProperty property, VRCExpressionParameters parameters)
                {
                    CompactMainContainer(property, parameters);

                    if (property == null) return;
                    switch ((VRCExpressionsMenu.Control.ControlType)property.FindPropertyRelative("type").intValue)
                    {
                        case VRCExpressionsMenu.Control.ControlType.RadialPuppet:
                            RadialContainer(property, parameters);
                            break;
                        case VRCExpressionsMenu.Control.ControlType.SubMenu:
                            SubMenuContainer(property);
                            break;
                        case VRCExpressionsMenu.Control.ControlType.TwoAxisPuppet:
                            CompactTwoAxisParametersContainer(property, parameters);
                            //AxisCustomisationContainer(property);
                            break;
                        case VRCExpressionsMenu.Control.ControlType.FourAxisPuppet:
                            CompactFourAxisParametersContainer(property, parameters);
                            //AxisCustomisationContainer(property);
                            break;
                        case VRCExpressionsMenu.Control.ControlType.Button:
                            break;
                        case VRCExpressionsMenu.Control.ControlType.Toggle:
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }

                #region Main container

                private static void MainContainer(SerializedProperty property)
                {
                    var rect = EditorGUILayout
                        .GetControlRect(false, 147);
                    VrcsdkPlusToolbox.Container.GUIBox(ref rect);

                    var nameRect = new Rect(rect.x, rect.y, rect.width - IconSpace, 21);
                    var typeRect = new Rect(rect.x, rect.y + 24, rect.width - IconSpace, 21);
                    var baseStyleRect = new Rect(rect.x, rect.y + 48, rect.width - IconSpace, 21);
                    var iconRect = new Rect(rect.x + rect.width - IconSize, rect.y, IconSize, IconSize);
                    var helpRect = new Rect(rect.x, rect.y + IconSpace, rect.width, 42);

                    DrawName(nameRect, property, true);
                    DrawType(typeRect, property, true);
                    DrawStyle(baseStyleRect, property, true);
                    DrawIcon(iconRect, property);
                    DrawHelp(helpRect, property);
                }

                private static void CompactMainContainer(SerializedProperty property, VRCExpressionParameters parameters)
                {
                    var rect = EditorGUILayout.GetControlRect(false, 66);
                    VrcsdkPlusToolbox.Container.GUIBox(ref rect);

                    var halfWidth = (rect.width - CompactIconSpace) / 2;
                    var nameRect = new Rect(rect.x, rect.y, halfWidth - 3, 18);
                    var typeRect = new Rect(rect.x + halfWidth, rect.y, halfWidth - 19, 18);
                    var helpRect = new Rect(typeRect.x + typeRect.width + 1, rect.y, 18, 18);
                    var parameterRect = new Rect(rect.x, rect.y + 21, rect.width - CompactIconSpace, 18);
                    var styleRect = new Rect(rect.x, rect.y + 42, rect.width - CompactIconSize, 18);
                    var iconRect = new Rect(rect.x + rect.width - CompactIconSize, rect.y, CompactIconSize, CompactIconSize);

                    DrawName(nameRect, property, false);
                    DrawType(typeRect, property, false);
                    DrawStyle(styleRect, property, false);

                    if (property != null)
                        GUI.Label(helpRect, new GUIContent(VrcsdkPlusToolbox.GUIContent.Help) { tooltip = GetHelpMessage(property) }, GUIStyle.none);

                    ParameterContainer(property, parameters, parameterRect);

                    DrawIcon(iconRect, property);

                    // ToDo Draw error help if Parameter not found
                }

                private static void DrawName(Rect rect, SerializedProperty property, bool drawLabel)
                {
                    if (property == null)
                    {
                        VrcsdkPlusToolbox.Placeholder.GUI(rect);
                        return;
                    }

                    var name = property.FindPropertyRelative("name");

                    if (drawLabel)
                    {
                        var label = new Rect(rect.x, rect.y, 100, rect.height);
                        rect = new Rect(rect.x + 103, rect.y, rect.width - 103, rect.height);

                        GUI.Label(label, "Name");
                    }

                    name.stringValue = EditorGUI.TextField(rect, name.stringValue);
                    if (string.IsNullOrEmpty(name.stringValue)) GUI.Label(rect, "Name", VrcsdkPlusToolbox.Styles.Label.PlaceHolder);
                }

                private static void DrawType(Rect rect, SerializedProperty property, bool drawLabel)
                {
                    if (property == null)
                    {
                        VrcsdkPlusToolbox.Placeholder.GUI(rect);
                        return;
                    }

                    if (drawLabel)
                    {
                        var label = new Rect(rect.x, rect.y, 100, rect.height);
                        rect = new Rect(rect.x + 103, rect.y, rect.width - 103, rect.height);

                        GUI.Label(label, "Type");
                    }

                    var controlType = property.FindPropertyRelative("type").ToControlType();
                    var newType = (VRCExpressionsMenu.Control.ControlType)EditorGUI.EnumPopup(rect, controlType);

                    if (newType != controlType)
                        ConversionEntry(property, controlType, newType);
                }

                private static void DrawStyle(Rect rect, SerializedProperty property, bool drawLabel)
                {
                    const float toggleSize = 21;

                    if (property == null)
                    {
                        VrcsdkPlusToolbox.Placeholder.GUI(rect);
                        return;
                    }

                    if (drawLabel)
                    {
                        var labelRect = new Rect(rect.x, rect.y, 100, rect.height);
                        rect = new Rect(rect.x + 103, rect.y, rect.width - 103, rect.height);
                        GUI.Label(labelRect, "Style");
                    }

                    var colorRect = new Rect(rect.x, rect.y, rect.width - (toggleSize + 3) * 2, rect.height);
                    var boldRect = new Rect(colorRect.x + colorRect.width, rect.y, toggleSize, rect.height);
                    var italicRect = new Rect(boldRect); italicRect.x += italicRect.width + 3; boldRect.width = toggleSize;
                    var rawName = property.FindPropertyRelative("name").stringValue;
                    var textColor = Color.white;

                    var isBold = rawName.Contains("<b>") && rawName.Contains("</b>");
                    var isItalic = rawName.Contains("<i>") && rawName.Contains("</i>");
                    var m = Regex.Match(rawName, "<color=(#[0-9|A-F]{6,8})>");
                    if (m.Success)
                    {
                        if (rawName.Contains("</color>"))
                        {
                            if (ColorUtility.TryParseHtmlString(m.Groups[1].Value, out var newColor))
                                textColor = newColor;

                        }
                    }


                    EditorGUI.BeginChangeCheck();
                    textColor = EditorGUI.ColorField(colorRect, textColor);
                    if (EditorGUI.EndChangeCheck())
                    {
                        rawName = Regex.Replace(rawName, "</?color=?.*?>", string.Empty);
                        rawName = $"<color=#{ColorUtility.ToHtmlStringRGB(textColor)}>{rawName}</color>";
                    }

                    w_MakeRectLinkCursor(boldRect);
                    EditorGUI.BeginChangeCheck();
                    isBold = GUI.Toggle(boldRect, isBold, new GUIContent("<b>b</b>","Bold"), VrcsdkPlusToolbox.Styles.LetterButton);
                    if (EditorGUI.EndChangeCheck()) SetCharTag('b', isBold);

                    w_MakeRectLinkCursor(italicRect);
                    EditorGUI.BeginChangeCheck();
                    isItalic = GUI.Toggle(italicRect, isItalic, new GUIContent("<i>i</i>", "Italic"), VrcsdkPlusToolbox.Styles.LetterButton);
                    if (EditorGUI.EndChangeCheck()) SetCharTag('i', isItalic);


                    property.FindPropertyRelative("name").stringValue = rawName;
                    return;

                    void SetCharTag(char c, bool state)
                    {
                        rawName = !state ?
                            Regex.Replace(rawName, $"</?{c}>", string.Empty) : 
                            $"<{c}>{rawName}</{c}>";
                    }
                }

                private static void DrawIcon(Rect rect, SerializedProperty property)
                {
                    if (property == null)
                        VrcsdkPlusToolbox.Placeholder.GUI(rect);
                    else
                    {
                        var value = property.FindPropertyRelative("icon");

                        value.objectReferenceValue = EditorGUI.ObjectField(
                            rect,
                            string.Empty,
                            value.objectReferenceValue,
                            typeof(Texture2D),
                            false
                        );
                    }
                }

                private static void DrawHelp(Rect rect, SerializedProperty property)
                {
                    if (property == null)
                    {
                        VrcsdkPlusToolbox.Placeholder.GUI(rect);
                        return;
                    }

                    var message = GetHelpMessage(property);
                    EditorGUI.HelpBox(rect, message, MessageType.Info);
                }

                private static string GetHelpMessage(SerializedProperty property)
                {
                    return property.FindPropertyRelative("type").ToControlType() switch
                    {
                        VRCExpressionsMenu.Control.ControlType.Button =>
                            "Click or hold to activate. The button remains active for a minimum 0.2s.\nWhile active the (Parameter) is set to (Value).\nWhen inactive the (Parameter) is reset to zero.",
                        VRCExpressionsMenu.Control.ControlType.Toggle =>
                            "Click to toggle on or off.\nWhen turned on the (Parameter) is set to (Value).\nWhen turned off the (Parameter) is reset to zero.",
                        VRCExpressionsMenu.Control.ControlType.SubMenu =>
                            "Opens another expression menu.\nWhen opened the (Parameter) is set to (Value).\nWhen closed (Parameter) is reset to zero.",
                        VRCExpressionsMenu.Control.ControlType.TwoAxisPuppet =>
                            "Puppet menu that maps the joystick to two parameters (-1 to +1).\nWhen opened the (Parameter) is set to (Value).\nWhen closed (Parameter) is reset to zero.",
                        VRCExpressionsMenu.Control.ControlType.FourAxisPuppet =>
                            "Puppet menu that maps the joystick to four parameters (0 to 1).\nWhen opened the (Parameter) is set to (Value).\nWhen closed (Parameter) is reset to zero.",
                        VRCExpressionsMenu.Control.ControlType.RadialPuppet =>
                            "Puppet menu that sets a value based on joystick rotation. (0 to 1)\nWhen opened the (Parameter) is set to (Value).\nWhen closed (Parameter) is reset to zero.",
                        _ => "ERROR: Unable to load message - Invalid control type"
                    };
                }

                #endregion

                #region Type Conversion

                private static void ConversionEntry(SerializedProperty property, VRCExpressionsMenu.Control.ControlType tOld, VRCExpressionsMenu.Control.ControlType tNew)
                {
                    // Is old one button / toggle, and new one not?
                    if (
                            tOld is VRCExpressionsMenu.Control.ControlType.Button or VRCExpressionsMenu.Control.ControlType.Toggle &&
                            tNew != VRCExpressionsMenu.Control.ControlType.Button && tNew != VRCExpressionsMenu.Control.ControlType.Toggle
                        )
                        // Reset parameter
                        property.FindPropertyRelative("parameter").FindPropertyRelative("name").stringValue = "";
                    else if (
                        tOld != VRCExpressionsMenu.Control.ControlType.Button && tOld != VRCExpressionsMenu.Control.ControlType.Toggle &&
                        tNew is VRCExpressionsMenu.Control.ControlType.Button or VRCExpressionsMenu.Control.ControlType.Toggle
                    )
                        SetupSubParameters(property, tNew);

                    // Is either a submenu
                    if (tOld == VRCExpressionsMenu.Control.ControlType.SubMenu || tNew == VRCExpressionsMenu.Control.ControlType.SubMenu)
                        SetupSubParameters(property, tNew);

                    // Is either Puppet)
                    if (IsPuppetConversion(tOld, tNew))
                        DoPuppetConversion(property, tNew);
                    else if (
                        tNew is VRCExpressionsMenu.Control.ControlType.RadialPuppet or VRCExpressionsMenu.Control.ControlType.TwoAxisPuppet or VRCExpressionsMenu.Control.ControlType.FourAxisPuppet
                    )
                        SetupSubParameters(property, tNew);

                    property.FindPropertyRelative("type").enumValueIndex = tNew.GetEnumValueIndex();
                }

                private static bool IsPuppetConversion(VRCExpressionsMenu.Control.ControlType tOld, VRCExpressionsMenu.Control.ControlType tNew)
                {
                    return tOld is VRCExpressionsMenu.Control.ControlType.RadialPuppet or VRCExpressionsMenu.Control.ControlType.TwoAxisPuppet or VRCExpressionsMenu.Control.ControlType.FourAxisPuppet &&
                           tNew is VRCExpressionsMenu.Control.ControlType.RadialPuppet or VRCExpressionsMenu.Control.ControlType.TwoAxisPuppet or VRCExpressionsMenu.Control.ControlType.FourAxisPuppet;
                }

                private static void DoPuppetConversion(SerializedProperty property, VRCExpressionsMenu.Control.ControlType tNew)
                {
                    var subParameters = property.FindPropertyRelative("subParameters");
                    var sub0 = subParameters.GetArrayElementAtIndex(0).FindPropertyRelative("name").stringValue;
                    var sub1 = subParameters.arraySize > 1
                        ? subParameters.GetArrayElementAtIndex(1).FindPropertyRelative("name").stringValue
                        : string.Empty;

                    subParameters.ClearArray();
                    subParameters.InsertArrayElementAtIndex(0);
                    subParameters.GetArrayElementAtIndex(0).FindPropertyRelative("name").stringValue = sub0;

                    // ReSharper disable once SwitchStatementMissingSomeEnumCasesNoDefault
                    switch (tNew)
                    {
                        case VRCExpressionsMenu.Control.ControlType.TwoAxisPuppet:
                            subParameters.InsertArrayElementAtIndex(1);
                            subParameters.GetArrayElementAtIndex(1).FindPropertyRelative("name").stringValue = sub1;
                            break;

                        case VRCExpressionsMenu.Control.ControlType.FourAxisPuppet:
                            subParameters.InsertArrayElementAtIndex(1);
                            subParameters.GetArrayElementAtIndex(1).FindPropertyRelative("name").stringValue = sub1;
                            subParameters.InsertArrayElementAtIndex(2);
                            subParameters.GetArrayElementAtIndex(2).FindPropertyRelative("name").stringValue = "";
                            subParameters.InsertArrayElementAtIndex(3);
                            subParameters.GetArrayElementAtIndex(3).FindPropertyRelative("name").stringValue = "";
                            break;
                    }
                }

                private static void SetupSubParameters(SerializedProperty property, VRCExpressionsMenu.Control.ControlType type)
                {
                    var subParameters = property.FindPropertyRelative("subParameters");
                    subParameters.ClearArray();

                    switch (type)
                    {
                        case VRCExpressionsMenu.Control.ControlType.RadialPuppet:
                        case VRCExpressionsMenu.Control.ControlType.SubMenu:
                            subParameters.InsertArrayElementAtIndex(0);
                            break;
                        case VRCExpressionsMenu.Control.ControlType.TwoAxisPuppet:
                            subParameters.InsertArrayElementAtIndex(0);
                            subParameters.InsertArrayElementAtIndex(1);
                            break;
                        case VRCExpressionsMenu.Control.ControlType.FourAxisPuppet:
                            subParameters.InsertArrayElementAtIndex(0);
                            subParameters.InsertArrayElementAtIndex(1);
                            subParameters.InsertArrayElementAtIndex(2);
                            subParameters.InsertArrayElementAtIndex(3);
                            break;
                        case VRCExpressionsMenu.Control.ControlType.Button:
                            break;
                        case VRCExpressionsMenu.Control.ControlType.Toggle:
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(type), type, null);
                    }
                }

                #endregion

                /*static void DrawParameterNotFound(string parameter)
                {
                    EditorGUILayout.HelpBox(
                        $"Parameter not found on the active avatar descriptor ({parameter})",
                        MessageType.Warning
                    );
                }*/



                #region BuildParameterArray

                private static void BuildParameterArray(
                    string name,
                    VRCExpressionParameters parameters,
                    out int index,
                    out string[] parametersAsString
                )
                {
                    index = -2;
                    if (!parameters)
                    {
                        parametersAsString = Array.Empty<string>();
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        for (var i = 0; i < parameters.parameters.Length; i++)
                        {
                            if (parameters.parameters[i].name != name) continue;

                            index = i + 1;
                            break;
                        }
                    }
                    else
                        index = -1;

                    parametersAsString = new string[parameters.parameters.Length + 1];
                    parametersAsString[0] = "[None]";
                    for (var i = 0; i < parameters.parameters.Length; i++)
                    {
                        parametersAsString[i + 1] = parameters.parameters[i].valueType switch
                        {
                            VRCExpressionParameters.ValueType.Int => $"{parameters.parameters[i].name} [int]",
                            VRCExpressionParameters.ValueType.Float => $"{parameters.parameters[i].name} [float]",
                            VRCExpressionParameters.ValueType.Bool => $"{parameters.parameters[i].name} [bool]",
                            _ => parametersAsString[i + 1]
                        };
                    }
                }

                private static void BuildParameterArray(
                    string name,
                    VRCExpressionParameters parameters,
                    out int index,
                    out VRCExpressionParameters.Parameter[] filteredParameters,
                    out string[] filteredParametersAsString,
                    VRCExpressionParameters.ValueType filter
                )
                {
                    index = -2;
                    if (!parameters)
                    {
                        filteredParameters = Array.Empty<VRCExpressionParameters.Parameter>();
                        filteredParametersAsString = Array.Empty<string>();
                        return;
                    }

                    filteredParameters = parameters.parameters.Where(p => p.valueType == filter).ToArray();

                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        for (var i = 0; i < filteredParameters.Length; i++)
                        {
                            if (filteredParameters[i].name != name) continue;

                            index = i + 1;
                            break;
                        }
                    }
                    else
                        index = -1;

                    filteredParametersAsString = new string[filteredParameters.Length + 1];
                    filteredParametersAsString[0] = "[None]";
                    for (var i = 0; i < filteredParameters.Length; i++)
                    {
                        filteredParametersAsString[i + 1] = filteredParameters[i].valueType switch
                        {
                            VRCExpressionParameters.ValueType.Int => $"{filteredParameters[i].name} [int]",
                            VRCExpressionParameters.ValueType.Float => $"{filteredParameters[i].name} [float]",
                            VRCExpressionParameters.ValueType.Bool => $"{filteredParameters[i].name} [bool]",
                            _ => throw new ArgumentOutOfRangeException()
                        };
                    }
                }

                #endregion

                #region DrawParameterSelector

                private struct ParameterSelectorOptions
                {
                    public Action ExtraGUI;
                    public Rect Rect;
                    public bool Required;

                    public ParameterSelectorOptions(Rect rect, bool required, Action extraGUI = null)
                    {
                        Required = required;
                        Rect = rect;
                        ExtraGUI = extraGUI;
                    }

                    public ParameterSelectorOptions(Rect rect, Action extraGUI = null)
                    {
                        Required = false;
                        Rect = rect;
                        ExtraGUI = extraGUI;
                    }

                    public ParameterSelectorOptions(bool required, Action extraGUI = null)
                    {
                        Required = required;
                        Rect = default;
                        ExtraGUI = extraGUI;
                    }
                }

                private static bool DrawParameterSelector(
                    string label,
                    SerializedProperty property,
                    VRCExpressionParameters parameters,
                    ParameterSelectorOptions options = default
                )
                {
                    BuildParameterArray(
                        property.FindPropertyRelative("name").stringValue,
                        parameters,
                        out var index,
                        out var parametersAsString
                    );
                    return DrawParameterSelection__BASE(
                        label,
                        property,
                        index,
                        parameters,
                        parameters?.parameters,
                        parametersAsString,
                        false,
                        options
                    );
                }

                private static void DrawParameterSelector(string label,
                    SerializedProperty property,
                    VRCExpressionParameters parameters,
                    VRCExpressionParameters.ValueType filter,
                    ParameterSelectorOptions options = default)
                {
                    BuildParameterArray(
                        property.FindPropertyRelative("name").stringValue,
                        parameters,
                        out var index,
                        out var filteredParameters,
                        out var parametersAsString,
                        filter
                    );
                    DrawParameterSelection__BASE(
                        label,
                        property,
                        index,
                        parameters,
                        filteredParameters,
                        parametersAsString,
                        true,
                        options
                    );
                }

                private static bool DrawParameterSelection__BASE(
                    string label,
                    SerializedProperty property,
                    int index,
                    VRCExpressionParameters targetParameters,
                    VRCExpressionParameters.Parameter[] parameters,
                    string[] parametersAsString,
                    bool isFiltered,
                    ParameterSelectorOptions options
                )
                {
                    var isEmpty = index == -1;
                    var isMissing = index == -2;
                    var willWarn = isMissing || options.Required && isEmpty;
                    var parameterName = property.FindPropertyRelative("name").stringValue;
                    var warnMsg = targetParameters ? isMissing ? isFiltered ?
                                $"Parameter ({parameterName}) not found or invalid" :
                                $"Parameter ({parameterName}) not found on the active avatar descriptor" :
                            "Parameter is blank. Control may be dysfunctional." :
                        VrcsdkPlusToolbox.GUIContent.MissingParametersTooltip;

                    var rectNotProvided = options.Rect == default;
                    using (new GUILayout.HorizontalScope())
                    {
                        const float contentAddWidth = 50;
                        const float contentWarnWidth = 18;
                        const float contentDropdownWidth = 20;
                        //const float CONTENT_TEXT_FIELD_PORTION = 0.25f;
                        const float missingFullWidth = contentAddWidth + contentWarnWidth + 2;

                        var hasLabel = !string.IsNullOrEmpty(label);

                        if (rectNotProvided) options.Rect = EditorGUILayout.GetControlRect(false, 18);

                        var name = property.FindPropertyRelative("name");

                        var labelRect = new Rect(options.Rect) { width = hasLabel ? 120 : 0 };
                        var textfieldRect = new Rect(labelRect) { x = labelRect.x + labelRect.width, width = options.Rect.width - labelRect.width - contentDropdownWidth - 2 };
                        var dropdownRect = new Rect(textfieldRect) { x = textfieldRect.x + textfieldRect.width, width = contentDropdownWidth };
                        var addRect = Rect.zero;
                        var warnRect = Rect.zero;

                        if (targetParameters && isMissing)
                        {
                            textfieldRect.width -= missingFullWidth;
                            dropdownRect.x -= missingFullWidth;
                            addRect = new Rect(options.Rect) { x = textfieldRect.x + textfieldRect.width + contentDropdownWidth + 2, width = contentAddWidth };
                            warnRect = new Rect(addRect) { x = addRect.x + addRect.width, width = contentWarnWidth };
                        }
                        else if (!targetParameters || options.Required && isEmpty)
                        {
                            textfieldRect.width -= contentWarnWidth;
                            dropdownRect.x -= contentWarnWidth;
                            warnRect = new Rect(dropdownRect) { x = dropdownRect.x + dropdownRect.width, width = contentWarnWidth };
                        }

                        if (hasLabel) GUI.Label(labelRect, label);
                        using (new EditorGUI.DisabledScope(!targetParameters || parametersAsString.Length <= 1))
                        {
                            var newIndex = EditorGUI.Popup(dropdownRect, string.Empty, index, parametersAsString);
                            if (index != newIndex)
                                name.stringValue = newIndex == 0 ? string.Empty : parameters[newIndex - 1].name;
                        }

                        name.stringValue = EditorGUI.TextField(textfieldRect, name.stringValue);
                        if (string.IsNullOrEmpty(name.stringValue)) GUI.Label(textfieldRect, "Parameter", VrcsdkPlusToolbox.Styles.Label.PlaceHolder);
                        if (willWarn) GUI.Label(warnRect, new GUIContent(VrcsdkPlusToolbox.GUIContent.Warn) { tooltip = warnMsg });

                        if (isMissing)
                        {
                            int dummy;

                            if (!isFiltered)
                            {
                                dummy = EditorGUI.Popup(addRect, -1, Enum.GetNames(typeof(VRCExpressionParameters.ValueType)));

                                addRect.x += 3;
                                GUI.Label(addRect, "Add");
                            }
                            else dummy = GUI.Button(addRect, "Add") ? 1 : -1;

                            if (dummy != -1)
                            {
                                var so = new SerializedObject(targetParameters);
                                var param = so.FindProperty("parameters");
                                var prop = param.GetArrayElementAtIndex(param.arraySize++);
                                prop.FindPropertyRelative("valueType").enumValueIndex = dummy;
                                prop.FindPropertyRelative("name").stringValue = name.stringValue;
                                prop.FindPropertyRelative("saved").boolValue = true;
                                try{ prop.FindPropertyRelative("networkSynced").boolValue = true; } catch{}
                                so.ApplyModifiedProperties();
                            }
                        }

                        options.ExtraGUI?.Invoke();
                    }

                    return isMissing;
                }

                #endregion

                #region Parameter conainer

                private static void ParameterContainer(
                    SerializedProperty property,
                    VRCExpressionParameters parameters,
                    Rect rect = default
                )
                {
                    var rectProvided = rect != default;

                    if (property?.FindPropertyRelative("parameter") == null)
                    {
                        if (rectProvided)
                            VrcsdkPlusToolbox.Placeholder.GUI(rect);
                        else
                        {
                            VrcsdkPlusToolbox.Container.BeginLayout();
                            VrcsdkPlusToolbox.Placeholder.GUILayout(18);
                            VrcsdkPlusToolbox.Container.EndLayout();
                        }
                    }
                    else
                    {
                        if (!rectProvided) VrcsdkPlusToolbox.Container.BeginLayout();

                        const float contentValueSelectorWidth = 50;
                        Rect selectorRect = default;
                        Rect valueRect = default;

                        if (rectProvided)
                        {
                            selectorRect = new Rect(rect.x, rect.y, rect.width - contentValueSelectorWidth - 3,
                                rect.height);
                            valueRect = new Rect(selectorRect.x + selectorRect.width + 3, rect.y,
                                contentValueSelectorWidth, rect.height);
                        }

                        var parameter = property.FindPropertyRelative("parameter");

                        var t = (VRCExpressionsMenu.Control.ControlType)property.FindPropertyRelative("type").intValue;
                        var isRequired = t is VRCExpressionsMenu.Control.ControlType.Button or VRCExpressionsMenu.Control.ControlType.Toggle;
                        DrawParameterSelector(rectProvided ? string.Empty : "Parameter", parameter, parameters, new ParameterSelectorOptions()
                        {
                            Rect = selectorRect,
                            Required = isRequired,
                            ExtraGUI = () =>
                            {
                                #region Value selector

                                var parameterName = parameter.FindPropertyRelative("name");
                                var param = parameters?.parameters.FirstOrDefault(p => p.name == parameterName.stringValue);

                                // Check what type the parameter is

                                var value = property.FindPropertyRelative("value");
                                switch (param?.valueType)
                                {
                                    case VRCExpressionParameters.ValueType.Int:
                                        value.floatValue = Mathf.Clamp(rectProvided ?
                                            EditorGUI.IntField(valueRect, (int)value.floatValue) :
                                            EditorGUILayout.IntField((int)value.floatValue, GUILayout.Width(contentValueSelectorWidth)), 0f, 255f);
                                        break;

                                    case VRCExpressionParameters.ValueType.Float:
                                        value.floatValue = Mathf.Clamp(rectProvided ?
                                            EditorGUI.FloatField(valueRect, value.floatValue) :
                                            EditorGUILayout.FloatField(value.floatValue, GUILayout.Width(contentValueSelectorWidth)), -1, 1);
                                        break;

                                    case VRCExpressionParameters.ValueType.Bool:
                                        using (new EditorGUI.DisabledScope(true))
                                        {
                                            if (rectProvided) EditorGUI.TextField(valueRect, string.Empty);
                                            else EditorGUILayout.TextField(string.Empty, GUILayout.Width(contentValueSelectorWidth));
                                        }

                                        value.floatValue = 1f;
                                        break;

                                    default:
                                        value.floatValue = Mathf.Clamp(rectProvided ?
                                            EditorGUI.FloatField(valueRect, value.floatValue) :
                                            EditorGUILayout.FloatField(value.floatValue, GUILayout.Width(contentValueSelectorWidth)), -1, 255);
                                        break;
                                }
                                #endregion
                            }
                        });

                        if (!rectProvided)
                            VrcsdkPlusToolbox.Container.EndLayout();
                    }
                }

                #endregion

                #region Miscellaneous containers

                private static void RadialContainer(SerializedProperty property, VRCExpressionParameters parameters)
                {
                    using (new VrcsdkPlusToolbox.Container.Vertical())
                        DrawParameterSelector(
                            "Rotation",
                            property.FindPropertyRelative("subParameters").GetArrayElementAtIndex(0),
                            parameters,
                            VRCExpressionParameters.ValueType.Float,
                            new ParameterSelectorOptions(true)
                        );
                }

                private static void SubMenuContainer(SerializedProperty property)
                {
                    using (new VrcsdkPlusToolbox.Container.Vertical())
                    {
                        var subMenu = property.FindPropertyRelative("subMenu");
                        var nameProperty = property.FindPropertyRelative("name");
                        var emptySubmenu = subMenu.objectReferenceValue == null;

                        using (new GUILayout.HorizontalScope())
                        {
                            EditorGUILayout.PropertyField(subMenu);
                            if (emptySubmenu)
                            {
                                using (new EditorGUI.DisabledScope(_currentNode?.Value == null))
                                    if (GUILayout.Button("New", GUILayout.Width(40)))
                                    {
                                        var m = _currentNode?.Value;
                                        var path = AssetDatabase.GetAssetPath(m);
                                        if (string.IsNullOrEmpty(path))
                                            path = $"Assets/{m?.name}.asset";
                                        var parentPath = Path.GetDirectoryName(path);
                                        var assetName = string.IsNullOrEmpty(nameProperty?.stringValue) ? $"{m?.name} SubMenu.asset" : $"{nameProperty.stringValue} Menu.asset";
                                        var newMenuPath = VrcsdkPlusToolbox.ReadyAssetPath(parentPath, assetName, true);

                                        var newMenu = CreateInstance<VRCExpressionsMenu>();
                                        newMenu.controls ??= new List<VRCExpressionsMenu.Control>();

                                        AssetDatabase.CreateAsset(newMenu, newMenuPath);
                                        subMenu.objectReferenceValue = newMenu;
                                    }
                                GUILayout.Label(new GUIContent(VrcsdkPlusToolbox.GUIContent.Warn) { tooltip = "Submenu is empty. This control has no use." }, VrcsdkPlusToolbox.Styles.Icon);
                            }
                            using (new EditorGUI.DisabledScope(emptySubmenu))
                            {
                                if (ClickableButton(VrcsdkPlusToolbox.GUIContent.Folder, VrcsdkPlusToolbox.Styles.Icon))
                                    Selection.activeObject = subMenu.objectReferenceValue;
                                if (ClickableButton(VrcsdkPlusToolbox.GUIContent.Clear, VrcsdkPlusToolbox.Styles.Icon))
                                    subMenu.objectReferenceValue = null;
                            }
                        }
                    }
                }

                private static void CompactTwoAxisParametersContainer(SerializedProperty property, VRCExpressionParameters parameters)
                {
                    using (new VrcsdkPlusToolbox.Container.Vertical())
                    {
                        using (new GUILayout.HorizontalScope())
                        {
                            using (new GUILayout.HorizontalScope())
                                GUILayout.Label("Axis Parameters", VrcsdkPlusToolbox.Styles.Label.Centered);


                            using (new GUILayout.HorizontalScope())
                            {
                                GUILayout.Label("Name -", VrcsdkPlusToolbox.Styles.Label.Centered);
                                GUILayout.Label("Name +", VrcsdkPlusToolbox.Styles.Label.Centered);
                            }
                        }

                        var subs = property.FindPropertyRelative("subParameters");
                        var sub0 = subs.GetArrayElementAtIndex(0);
                        var sub1 = subs.GetArrayElementAtIndex(1);

                        var labels = SafeGetLabels(property);

                        using (new GUILayout.HorizontalScope())
                        {
                            var rect = EditorGUILayout.GetControlRect();
                            using (new GUILayout.HorizontalScope())
                            {
                                DrawParameterSelector(
                                    "Horizontal",
                                    sub0,
                                    parameters,
                                    VRCExpressionParameters.ValueType.Float,
                                    new ParameterSelectorOptions(rect, true)
                                );
                            }

                            using (new GUILayout.HorizontalScope())
                            {
                                DrawLabel(labels.GetArrayElementAtIndex(0), "Left");
                                DrawLabel(labels.GetArrayElementAtIndex(1), "Right");
                            }
                        }

                        using (new GUILayout.HorizontalScope())
                        {
                            var rect = EditorGUILayout.GetControlRect();
                            using (new GUILayout.HorizontalScope())
                            {
                                DrawParameterSelector(
                                    "Vertical",
                                    sub1,
                                    parameters,
                                    VRCExpressionParameters.ValueType.Float,
                                    new ParameterSelectorOptions(rect, true)
                                );
                            }

                            using (new GUILayout.HorizontalScope())
                            {
                                DrawLabel(labels.GetArrayElementAtIndex(2), "Down");
                                DrawLabel(labels.GetArrayElementAtIndex(3), "Up");
                            }
                        }
                    }

                }

                private static void CompactFourAxisParametersContainer(SerializedProperty property, VRCExpressionParameters parameters)
                {
                    using (new VrcsdkPlusToolbox.Container.Vertical())
                    {
                        using (new GUILayout.HorizontalScope())
                        {
                            var headerRect = EditorGUILayout.GetControlRect();
                            var r1 = new Rect(headerRect) { width = headerRect.width / 2 };
                            var r2 = new Rect(r1) { x = r1.x + r1.width };
                            GUI.Label(r1, "Axis Parameters", VrcsdkPlusToolbox.Styles.Label.Centered);
                            GUI.Label(r2, "Name", VrcsdkPlusToolbox.Styles.Label.Centered);
                        }

                        var subs = property.FindPropertyRelative("subParameters");
                        var sub0 = subs.GetArrayElementAtIndex(0);
                        var sub1 = subs.GetArrayElementAtIndex(1);
                        var sub2 = subs.GetArrayElementAtIndex(2);
                        var sub3 = subs.GetArrayElementAtIndex(3);

                        var labels = SafeGetLabels(property);

                        using (new GUILayout.HorizontalScope())
                        {
                            var r = EditorGUILayout.GetControlRect();
                            using (new GUILayout.HorizontalScope())
                            {
                                DrawParameterSelector(
                                    "Up",
                                    sub0,
                                    parameters,
                                    VRCExpressionParameters.ValueType.Float,
                                    new ParameterSelectorOptions(r, true)
                                );
                            }

                            using (new GUILayout.HorizontalScope())
                                DrawLabel(labels.GetArrayElementAtIndex(0), "Name");
                        }

                        using (new GUILayout.HorizontalScope())
                        {
                            var r = EditorGUILayout.GetControlRect();
                            using (new GUILayout.HorizontalScope())
                            {
                                DrawParameterSelector(
                                    "Right",
                                    sub1,
                                    parameters,
                                    VRCExpressionParameters.ValueType.Float,
                                    new ParameterSelectorOptions(r, true)
                                );
                            }

                            using (new GUILayout.HorizontalScope())
                                DrawLabel(labels.GetArrayElementAtIndex(1), "Name");
                        }

                        using (new GUILayout.HorizontalScope())
                        {
                            var r = EditorGUILayout.GetControlRect();
                            using (new GUILayout.HorizontalScope())
                            {
                                DrawParameterSelector(
                                    "Down",
                                    sub2,
                                    parameters,
                                    VRCExpressionParameters.ValueType.Float,
                                    new ParameterSelectorOptions(r, true)
                                );
                            }

                            using (new GUILayout.HorizontalScope())
                                DrawLabel(labels.GetArrayElementAtIndex(2), "Name");
                        }

                        using (new GUILayout.HorizontalScope())
                        {
                            var r = EditorGUILayout.GetControlRect();
                            using (new GUILayout.HorizontalScope())
                            {
                                DrawParameterSelector(
                                    "Left",
                                    sub3,
                                    parameters,
                                    VRCExpressionParameters.ValueType.Float,
                                    new ParameterSelectorOptions(r, true)
                                );
                            }

                            using (new GUILayout.HorizontalScope())
                                DrawLabel(labels.GetArrayElementAtIndex(3), "Name");
                        }
                    }

                }

                private static void TwoAxisParametersContainer(SerializedProperty property, VRCExpressionParameters parameters)
                {
                    VrcsdkPlusToolbox.Container.BeginLayout();

                    GUILayout.Label("Axis Parameters", VrcsdkPlusToolbox.Styles.Label.Centered);

                    var subs = property.FindPropertyRelative("subParameters");
                    var sub0 = subs.GetArrayElementAtIndex(0);
                    var sub1 = subs.GetArrayElementAtIndex(1);

                    DrawParameterSelector(
                        "Horizontal",
                        sub0,
                        parameters,
                        VRCExpressionParameters.ValueType.Float,
                        new ParameterSelectorOptions(true)
                    );

                    DrawParameterSelector(
                        "Vertical",
                        sub1,
                        parameters,
                        VRCExpressionParameters.ValueType.Float,
                        new ParameterSelectorOptions(true)
                    );

                    VrcsdkPlusToolbox.Container.EndLayout();
                }

                private static void FourAxisParametersContainer(SerializedProperty property, VRCExpressionParameters parameters)
                {
                    VrcsdkPlusToolbox.Container.BeginLayout("Axis Parameters");

                    var subs = property.FindPropertyRelative("subParameters");
                    var sub0 = subs.GetArrayElementAtIndex(0);
                    var sub1 = subs.GetArrayElementAtIndex(1);
                    var sub2 = subs.GetArrayElementAtIndex(2);
                    var sub3 = subs.GetArrayElementAtIndex(3);

                    DrawParameterSelector(
                        "Up",
                        sub0,
                        parameters,
                        VRCExpressionParameters.ValueType.Float,
                        new ParameterSelectorOptions(true)
                    );

                    DrawParameterSelector(
                        "Right",
                        sub1,
                        parameters,
                        VRCExpressionParameters.ValueType.Float,
                        new ParameterSelectorOptions(true)
                    );

                    DrawParameterSelector(
                        "Down",
                        sub2,
                        parameters,
                        VRCExpressionParameters.ValueType.Float,
                        new ParameterSelectorOptions(true)
                    );

                    DrawParameterSelector(
                        "Left",
                        sub3,
                        parameters,
                        VRCExpressionParameters.ValueType.Float,
                        new ParameterSelectorOptions(true)
                    );

                    VrcsdkPlusToolbox.Container.EndLayout();
                }

                private static void AxisCustomisationContainer(SerializedProperty property)
                {
                    var labels = SafeGetLabels(property);

                    using (new VrcsdkPlusToolbox.Container.Vertical("Customization"))
                    {
                        DrawLabel(labels.GetArrayElementAtIndex(0), "Up");
                        DrawLabel(labels.GetArrayElementAtIndex(1), "Right");
                        DrawLabel(labels.GetArrayElementAtIndex(2), "Down");
                        DrawLabel(labels.GetArrayElementAtIndex(3), "Left");
                    }
                }

                private static SerializedProperty SafeGetLabels(SerializedProperty property)
                {
                    var labels = property.FindPropertyRelative("labels");

                    labels.arraySize = 4;
                    var l0 = labels.GetArrayElementAtIndex(0);
                    if (l0 == null)
                    {
                        var menu = (VRCExpressionsMenu)labels.serializedObject.targetObject;
                        var index = menu.controls.FindIndex(property.objectReferenceValue);
                        menu.controls[index].labels = new[]
                        {
                            new VRCExpressionsMenu.Control.Label(),
                            new VRCExpressionsMenu.Control.Label(),
                            new VRCExpressionsMenu.Control.Label(),
                            new VRCExpressionsMenu.Control.Label()
                        };
                    }

                    if (labels.GetArrayElementAtIndex(0) == null)
                        Debug.Log("ITEM IS NULL");

                    return labels;
                }

                private static void DrawLabel(SerializedProperty property, string type)
                {
                    var compact = VrcsdkPlusToolbox.Preferences.CompactMode;
                    float imgWidth = compact ? 28 : 58;
                    var imgHeight = compact ? EditorGUIUtility.singleLineHeight : 58;

                    var imgProperty = property.FindPropertyRelative("icon");
                    var nameProperty = property.FindPropertyRelative("name");
                    if (!compact) EditorGUILayout.BeginVertical("helpbox");

                    using (new GUILayout.HorizontalScope())
                    {
                        using (new GUILayout.VerticalScope())
                        {
                            if (!compact)
                                using (new EditorGUI.DisabledScope(true))
                                    EditorGUILayout.LabelField("Axis", type, VrcsdkPlusToolbox.Styles.Label.LabelDropdown);

                            EditorGUILayout.PropertyField(nameProperty, compact ? GUIContent.none : new GUIContent("Name"));
                            var nameRect = GUILayoutUtility.GetLastRect();
                            if (compact && string.IsNullOrEmpty(nameProperty.stringValue)) GUI.Label(nameRect, $"{type}", VrcsdkPlusToolbox.Styles.Label.PlaceHolder);
                        }

                        imgProperty.objectReferenceValue = EditorGUILayout.ObjectField(imgProperty.objectReferenceValue, typeof(Texture2D), false, GUILayout.Width(imgWidth), GUILayout.Height(imgHeight));
                    }

                    if (!compact) EditorGUILayout.EndHorizontal();

                }

                #endregion
            }

        }


        #region Helper Methods
        #region Clickables

        private static bool ClickableButton(string     label, GUIStyle                 style = null, params GUILayoutOption[] options) => ClickableButton(new GUIContent(label), style, options);

        private static bool ClickableButton(string     label, params GUILayoutOption[] options) => ClickableButton(new GUIContent(label), null, options);

        internal static bool ClickableButton(GUIContent label, params GUILayoutOption[] options) => ClickableButton(label,                 null, options);

        private static bool ClickableButton(GUIContent label, GUIStyle style = null, params GUILayoutOption[] options)
        {
            style ??= GUI.skin.button;
            var clicked = GUILayout.Button(label, style, options);
            if (GUI.enabled) w_MakeRectLinkCursor();
            return clicked;
        }

        private static void w_MakeRectLinkCursor(Rect rect = default)
        {
            if (!GUI.enabled) return;
            if (Event.current.type != EventType.Repaint) return;
            if (rect == default) rect = GUILayoutUtility.GetLastRect();
            EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);
        }
        internal static bool w_MakeRectClickable(Rect rect = default)
        {
            if (rect == default) rect = GUILayoutUtility.GetLastRect();
            w_MakeRectLinkCursor(rect);
            var e = Event.current;
            return e.type == EventType.MouseDown && e.button == 0 && rect.Contains(e.mousePosition);
        }
        #endregion

        private static void Link(string label, string url)
        {
            var bgcolor = GUI.backgroundColor;
            GUI.backgroundColor = Color.clear;

            if (GUILayout.Button(new GUIContent(label, url), VrcsdkPlusToolbox.Styles.Label.FaintLinkLabel))
                Application.OpenURL(url);
            w_UnderlineLastRectOnHover();
            
            GUI.backgroundColor = bgcolor;
        }

        private static void w_UnderlineLastRectOnHover(Color? color = null)
        {
            color ??= new Color(0.3f, 0.7f, 1);
            if (Event.current.type != EventType.Repaint) return;
            var rect = GUILayoutUtility.GetLastRect();
            var mp = Event.current.mousePosition;
            if (rect.Contains(mp)) EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - 1, rect.width, 1), color.Value);
            EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);

        }

        private static Type ExtendedGetType(string typeName)
        {
            var myType = Type.GetType(typeName);
            if (myType != null)
                return myType;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var types = assembly.GetTypes();
                myType = types.FirstOrDefault(t  => t.FullName == typeName);
                if (myType != null)
                    return myType;
                myType = types.FirstOrDefault(t => t.Name == typeName);
                if (myType != null)
                    return myType;
            }
            return null;
        }

        private static void RefreshAvatar(ref VRCAvatarDescriptor avatar, ref VRCAvatarDescriptor[] validAvatars, Action onAvatarChanged = null, Func<VRCAvatarDescriptor, bool> favoredAvatar = null)
        {
            validAvatars = Object.FindObjectsOfType<VRCAvatarDescriptor>();
            if (avatar) return;

            if (validAvatars.Length > 0)
            {
                if (favoredAvatar != null)
                    avatar = validAvatars.FirstOrDefault(favoredAvatar) ?? validAvatars[0];
                else avatar = validAvatars[0];
            }

            onAvatarChanged?.Invoke();
        }

        private static bool DrawAdvancedAvatarFull(ref VRCAvatarDescriptor avatar, VRCAvatarDescriptor[] validAvatars, Action onAvatarChanged = null, bool warnNonHumanoid = true, bool warnPrefab = true, bool warnDoubleFX = true, string label = "Avatar", string tooltip = "The Targeted VRCAvatar", Action extraGUI = null)
            => DrawAdvancedAvatarField(ref avatar, validAvatars, onAvatarChanged, label, tooltip, extraGUI) && DrawAdvancedAvatarWarning(avatar, warnNonHumanoid, warnPrefab, warnDoubleFX);

        private static VRCAvatarDescriptor DrawAdvancedAvatarField(ref VRCAvatarDescriptor avatar, VRCAvatarDescriptor[] validAvatars, Action onAvatarChanged = null, string label = "Avatar", string tooltip = "The Targeted VRCAvatar", Action extraGUI = null)
        {
            using (new GUILayout.HorizontalScope())
            {
                var avatarContent = new GUIContent(label, tooltip);
                if (validAvatars is not { Length: > 0 }) EditorGUILayout.LabelField(avatarContent, new GUIContent("No Avatar Descriptors Found"));
                else
                {
                    using var change = new EditorGUI.ChangeCheckScope();
                    var dummy = EditorGUILayout.Popup(avatarContent, avatar ? Array.IndexOf(validAvatars, avatar) : -1, validAvatars.Where(a => a).Select(x => x.name).ToArray());
                    if (change.changed)
                    {
                        avatar = validAvatars[dummy];
                        EditorGUIUtility.PingObject(avatar);
                        onAvatarChanged?.Invoke();
                    }
                }

                extraGUI?.Invoke();
            }
            return avatar;
        }

        private static bool DrawAdvancedAvatarWarning(VRCAvatarDescriptor avatar, bool warnNonHumanoid = true, bool warnPrefab = true, bool warnDoubleFX = true)
        {
            return (!warnPrefab || !DrawPrefabWarning(avatar)) && (!warnDoubleFX || !DrawDoubleFXWarning(avatar, warnNonHumanoid));
        }

        private static bool DrawPrefabWarning(VRCAvatarDescriptor avatar)
        {
            if (!avatar) return false;
            var isPrefab = PrefabUtility.IsPartOfAnyPrefab(avatar.gameObject);
            if (!isPrefab) return false;
            EditorGUILayout.HelpBox("Target Avatar is a part of a prefab. Prefab unpacking is required.", MessageType.Error);
            if (GUILayout.Button("Unpack")) PrefabUtility.UnpackPrefabInstance(avatar.gameObject, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            return true;
        }
        private static bool DrawDoubleFXWarning(VRCAvatarDescriptor avatar, bool warnNonHumanoid = true)
        {
            if (!avatar) return false;
            var layers = avatar.baseAnimationLayers;

            if (layers.Length > 3)
            {
                var isDoubled = layers[3].type == layers[4].type;
                if (!isDoubled) return false;
                EditorGUILayout.HelpBox("Your Avatar's Action playable layer is set as FX. This is an uncommon bug.", MessageType.Error);
                if (!GUILayout.Button("Fix")) return true;
                avatar.baseAnimationLayers[3].type = VRCAvatarDescriptor.AnimLayerType.Action;
                EditorUtility.SetDirty(avatar);

                return true;
            }

            if (warnNonHumanoid)
                EditorGUILayout.HelpBox("Your Avatar's descriptor is set as Non-Humanoid! Please make sure that your Avatar's rig is Humanoid.", MessageType.Error);
            return warnNonHumanoid;

        }

        private static void GreenLog(string msg) => Debug.Log($"<color=green>[VRCSDK+] </color>{msg}");
        #endregion

        #region Automated Methods
        private static void OverrideEditor(Type componentType, Type editorType)
        {
            var attributeType = Type.GetType("UnityEditor.CustomEditorAttributes, UnityEditor, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null");
            var monoEditorType = Type.GetType("UnityEditor.CustomEditorAttributes+MonoEditorType, UnityEditor, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null");
            var editorsField = attributeType?.GetField("kSCustomEditors", BindingFlags.Static | BindingFlags.NonPublic);
            var inspectorField = monoEditorType?.GetField("m_InspectorType", BindingFlags.Public | BindingFlags.Instance);
            var editorDictionary = editorsField?.GetValue(null) as IDictionary;
            var editorsList = editorDictionary?[componentType] as IList;
            inspectorField?.SetValue(editorsList?[0], editorType);

            var inspectorType = Type.GetType("UnityEditor.InspectorWindow, UnityEditor, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null");
            var myTestMethod = inspectorType?.GetMethod("RefreshInspectors", BindingFlags.NonPublic | BindingFlags.Static);
            myTestMethod?.Invoke(null, null);
        }


        [InitializeOnLoadMethod]
        private static void DelayCallOverride()
        {
            EditorApplication.delayCall -= InitialOverride;
            EditorApplication.delayCall += InitialOverride;
        }

        private static void InitialOverride()
        {
            EditorApplication.delayCall -= InitialOverride;

            var attributeType = Type.GetType("UnityEditor.CustomEditorAttributes, UnityEditor, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null");
            var editorsInitializedField = attributeType?.GetField("s_Initialized", BindingFlags.Static | BindingFlags.NonPublic);

            try
            {
                if (!(bool)editorsInitializedField?.GetValue(null)!)
                {
                    var rebuildEditorsMethod = attributeType.GetMethod("Rebuild", BindingFlags.Static | BindingFlags.NonPublic);
                    rebuildEditorsMethod?.Invoke(null, null);
                    editorsInitializedField.SetValue(null, true);
                }

                OverrideEditor(typeof(VRCExpressionParameters), typeof(VrcParamsPlus));
                OverrideEditor(typeof(VRCExpressionsMenu), typeof(VrcMenuPlus));
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Debug.LogError("[VRCSDK+] Failed to override editors!");
            }

        }

        #endregion

        #region Quick Avatar
        [MenuItem("CONTEXT/VRCAvatarDescriptor/[SDK+] Quick Setup", false, 650)]
        private static void QuickSetup(MenuCommand command)
        {
            var desc = (VRCAvatarDescriptor)command.context;
            var ani = desc.GetComponent<Animator>();
            var serialized = new SerializedObject(desc);

            if (ani)
            {
                var leftEye = ani.GetBoneTransform(HumanBodyBones.LeftEye);
                var rightEye = ani.GetBoneTransform(HumanBodyBones.RightEye);

                var root = desc.transform;
                float worldXPosition;
                float worldYPosition;
                float worldZPosition;
                #region View Position
                if (leftEye && rightEye)
                {
                    var betterLeft = leftEye.parent.Find("LeftEye");
                    var betterRight = rightEye.parent.Find("RightEye");
                    leftEye = betterLeft ? betterLeft : leftEye;
                    rightEye = betterRight ? betterRight : rightEye;
                    var added = (leftEye.position + rightEye.position) / 2;
                    worldXPosition = added.x;
                    worldYPosition = added.y;
                    worldZPosition = added.z;
                }
                else
                {
                    var headPosition = ani.GetBoneTransform(HumanBodyBones.Head).position;
                    worldXPosition = headPosition.x;
                    worldYPosition = headPosition.y + ((headPosition.y - root.position.y) * 1.0357f - (headPosition.y - root.position.y));
                    worldZPosition = 0;
                }

                var realView = root.InverseTransformPoint(new Vector3(worldXPosition, worldYPosition, worldZPosition));
                realView = new Vector3(Mathf.Approximately(realView.x, 0) ? 0 : realView.x, realView.y, (realView.z + 0.0547f * realView.y) / 2);

                serialized.FindProperty("ViewPosition").vector3Value = realView;
                #endregion

                #region Eyes

                if (leftEye && rightEye)
                {
                    var eyes = serialized.FindProperty("customEyeLookSettings");
                    serialized.FindProperty("enableEyeLook").boolValue = true;

                    eyes.FindPropertyRelative("leftEye").objectReferenceValue = leftEye;
                    eyes.FindPropertyRelative("rightEye").objectReferenceValue = rightEye;

                    #region Rotation Values
                    const float axisValue = 0.1305262f;
                    const float wValue = 0.9914449f;

                    var upValue = new Quaternion(-axisValue, 0, 0, wValue);
                    var downValue = new Quaternion(axisValue, 0, 0, wValue);
                    var rightValue = new Quaternion(0, axisValue, 0, wValue);
                    var leftValue = new Quaternion(0, -axisValue, 0, wValue);

                    var up = eyes.FindPropertyRelative("eyesLookingUp");
                    var right = eyes.FindPropertyRelative("eyesLookingRight");
                    var down = eyes.FindPropertyRelative("eyesLookingDown");
                    var left = eyes.FindPropertyRelative("eyesLookingLeft");

                    void SetLeftAndRight(SerializedProperty p, Quaternion v)
                    {
                        p.FindPropertyRelative("left").quaternionValue = v;
                        p.FindPropertyRelative("right").quaternionValue = v;
                    }

                    SetLeftAndRight(up, upValue);
                    SetLeftAndRight(right, rightValue);
                    SetLeftAndRight(down, downValue);
                    SetLeftAndRight(left, leftValue);
                    #endregion

                    #region Blinking
                    SkinnedMeshRenderer body = null;
                    for (var i = 0; i < desc.transform.childCount; i++)
                    {
                        if (body = desc.transform.GetChild(i).GetComponent<SkinnedMeshRenderer>())
                            break;
                    }

                    if (body && body.sharedMesh)
                    {
                        for (var i = 0; i < body.sharedMesh.blendShapeCount; i++)
                        {
                            if (body.sharedMesh.GetBlendShapeName(i) != "Blink") continue;

                            eyes.FindPropertyRelative("eyelidType").enumValueIndex = 2;
                            eyes.FindPropertyRelative("eyelidsSkinnedMesh").objectReferenceValue = body;

                            var blendShapes = eyes.FindPropertyRelative("eyelidsBlendshapes");
                            blendShapes.arraySize = 3;
                            blendShapes.FindPropertyRelative("Array.data[0]").intValue = i;
                            blendShapes.FindPropertyRelative("Array.data[1]").intValue = -1;
                            blendShapes.FindPropertyRelative("Array.data[2]").intValue = -1;
                            break;
                        }
                    }
                    #endregion
                }
                #endregion
            }

            serialized.ApplyModifiedProperties();
            EditorApplication.delayCall -= ForceCallAutoLipSync;
            EditorApplication.delayCall += ForceCallAutoLipSync;
        }

        private static void ForceCallAutoLipSync()
        {
            EditorApplication.delayCall -= ForceCallAutoLipSync;

            var descriptorEditor = Type.GetType("AvatarDescriptorEditor3, Assembly-CSharp-Editor, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null") ?? 
                                   Type.GetType("AvatarDescriptorEditor3, VRC.SDK3A.Editor, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null");

            if (descriptorEditor == null)
            {
                Debug.LogWarning("AvatarDescriptorEditor3 Type couldn't be found!");
                return;
            }
            
            var tempEditor = (UnityEditor.Editor)Resources.FindObjectsOfTypeAll(descriptorEditor)[0];
            descriptorEditor.GetMethod("AutoDetectLipSync", BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(tempEditor, null);
        }

        #endregion
    }

    internal static class VrcsdkPlusToolbox
    {
        #region Ready Paths
		internal enum PathOption
		{
			Normal,
			ForceFolder,
			ForceFile
		}

        private static string ReadyAssetPath(string path, bool makeUnique = false, PathOption pathOption = PathOption.Normal)
		{
			var forceFolder = pathOption == PathOption.ForceFolder;
			var forceFile = pathOption == PathOption.ForceFile;

			path = forceFile ? LegalizeName(path) : forceFolder ? LegalizePath(path) : LegalizeFullPath(path);
			var isFolder = forceFolder || (!forceFile && string.IsNullOrEmpty(Path.GetExtension(path)));

			if (isFolder)
			{
				if (!Directory.Exists(path))
				{
					Directory.CreateDirectory(path);
					AssetDatabase.ImportAsset(path);
				}
				else if (makeUnique)
				{
					path = AssetDatabase.GenerateUniqueAssetPath(path);
					Directory.CreateDirectory(path);
					AssetDatabase.ImportAsset(path);
				}
			}
			else
			{
				const string basePath = "Assets";
				var folderPath = Path.GetDirectoryName(path);
				var fileName = Path.GetFileName(path);

				if (string.IsNullOrEmpty(folderPath))
					folderPath = basePath;
				else if (!folderPath.StartsWith(Application.dataPath) && !folderPath.StartsWith(basePath))
					folderPath = $"{basePath}/{folderPath}";

				if (folderPath != basePath && !Directory.Exists(folderPath))
				{
					Directory.CreateDirectory(folderPath);
					AssetDatabase.ImportAsset(folderPath);
				}

				path = $"{folderPath}/{fileName}";
				if (makeUnique)
					path = AssetDatabase.GenerateUniqueAssetPath(path);

			}

			return path;
		}

		internal static string ReadyAssetPath(string folderPath, string fullNameOrExtension, bool makeUnique = false)
		{
			if (string.IsNullOrEmpty(fullNameOrExtension))
				return ReadyAssetPath(LegalizePath(folderPath), makeUnique, PathOption.ForceFolder);
			return string.IsNullOrEmpty(folderPath) ? ReadyAssetPath(LegalizeName(fullNameOrExtension), makeUnique, PathOption.ForceFile) : ReadyAssetPath($"{LegalizePath(folderPath)}/{LegalizeName(fullNameOrExtension)}", makeUnique);
        }
		internal static string ReadyAssetPath(Object buddyAsset, string fullNameOrExtension = "", bool makeUnique = true)
		{
			var buddyPath = AssetDatabase.GetAssetPath(buddyAsset);
			var folderPath = Path.GetDirectoryName(buddyPath);
			if (string.IsNullOrEmpty(fullNameOrExtension))
				fullNameOrExtension = Path.GetFileName(buddyPath);
            if (!fullNameOrExtension.StartsWith("."))
                return ReadyAssetPath(folderPath, fullNameOrExtension, makeUnique);
            var assetName = string.IsNullOrWhiteSpace(buddyAsset.name) ? "SomeAsset" : buddyAsset.name;
            fullNameOrExtension = $"{assetName}{fullNameOrExtension}";

            return ReadyAssetPath(folderPath, fullNameOrExtension, makeUnique);
		}

        private static string LegalizeFullPath(string path)
		{
			if (string.IsNullOrEmpty(path))
			{
				Debug.LogWarning("Legalizing empty path! Returned path as 'EmptyPath'");
				return "EmptyPath";
			}

			var ext = Path.GetExtension(path);
			var isFolder = string.IsNullOrEmpty(ext);
			if (isFolder) return LegalizePath(path);

			var folderPath = Path.GetDirectoryName(path);
			var fileName = LegalizeName(Path.GetFileNameWithoutExtension(path));

			if (string.IsNullOrEmpty(folderPath)) return $"{fileName}{ext}";
			folderPath = LegalizePath(folderPath);

			return $"{folderPath}/{fileName}{ext}";
		}

        private static string LegalizePath(string path)
		{
			var regexFolderReplace = Regex.Escape(new string(Path.GetInvalidPathChars()));

			path = path.Replace('\\', '/');
			if (path.IndexOf('/') > 0)
				path = string.Join("/", path.Split('/').Select(s => Regex.Replace(s, $"[{regexFolderReplace}]", "-")));

			return path;

		}

        private static string LegalizeName(string name)
		{
			var regexFileReplace = Regex.Escape(new string(Path.GetInvalidFileNameChars()));
			return string.IsNullOrEmpty(name) ? "Unnamed" : Regex.Replace(name, $"[{regexFileReplace}]", "-");
		}
		#endregion
        
        internal static bool TryGetActiveIndex(this ReorderableList orderList, out int index)
        {
            index = orderList.index;
            if (index < orderList.count && index >= 0) return true;
            index = -1;
            return false;
        }
        public static string GenerateUniqueString(string s, Func<string, bool> passCondition, bool addNumberIfMissing = true)
        {
            if (passCondition(s)) return s;
            var match = Regex.Match(s, @"(?=.*)(\d+)$");
            if (!match.Success && !addNumberIfMissing) return s;
            var numberString = match.Success ? match.Groups[1].Value : "1";
            if (!match.Success && !s.EndsWith(" ")) s += " ";
            var newString = Regex.Replace(s, @"(?=.*?)\d+$", string.Empty);
            while (!passCondition($"{newString}{numberString}")) 
                numberString = (int.Parse(numberString) + 1).ToString(new string('0', numberString.Length));
            
            return $"{newString}{numberString}";
        }
        public static class Container
        {
            public class Vertical : IDisposable
            {
                public Vertical(params GUILayoutOption[] options)
                    => EditorGUILayout.BeginVertical(GUI.skin.GetStyle("helpbox"), options);

                public Vertical(string title, params GUILayoutOption[] options)
                {
                    EditorGUILayout.BeginVertical(GUI.skin.GetStyle("helpbox"), options);

                    EditorGUILayout.LabelField(title, Styles.Label.Centered);
                }

                public void Dispose() => EditorGUILayout.EndVertical();
            }
            public class Horizontal : IDisposable
            {
                public Horizontal(params GUILayoutOption[] options)
                    => EditorGUILayout.BeginHorizontal(GUI.skin.GetStyle("helpbox"), options);

                public void Dispose() => EditorGUILayout.EndHorizontal();
            }

            public static void BeginLayout(params GUILayoutOption[] options)
                => EditorGUILayout.BeginVertical(GUI.skin.GetStyle("helpbox"), options);

            public static void BeginLayout(string title, params GUILayoutOption[] options)
            {
                EditorGUILayout.BeginVertical(GUI.skin.GetStyle("helpbox"), options);

                EditorGUILayout.LabelField(title, Styles.Label.Centered);
            }
            public static void EndLayout() => EditorGUILayout.EndVertical();

            public static void GUIBox(ref Rect rect)
            {
                GUI.Box(rect, "", GUI.skin.GetStyle("helpbox"));

                rect.x += 4;
                rect.width -= 8;
                rect.y += 3;
                rect.height -= 6;
            }
        }

        public static class Placeholder
        {

            public static void GUILayout(float height) =>
                GUI(EditorGUILayout.GetControlRect(false, height));

            public static void GUI(Rect rect) => GUI(rect, EditorGUIUtility.isProSkin ? 53 : 182);

            private static void GUI(Rect rect, float color)
            {
                EditorGUI.DrawTextureTransparent(rect, GetColorTexture(color));
            }
        }

        public static class Styles
        {
            public const float Padding = 3;

            public static class Label
            {
                internal static readonly GUIStyle Centered = new(GUI.skin.label) {alignment = TextAnchor.MiddleCenter};

                internal static readonly GUIStyle RichText = new(GUI.skin.label) {richText = true};
                

                internal static readonly GUIStyle Type
                    = new(GUI.skin.label)
                    {
                        alignment = TextAnchor.MiddleRight,
                        normal =
                        {
                            textColor = EditorGUIUtility.isProSkin ? Color.gray : BrightnessToColor(91),
                        },
                        fontStyle = FontStyle.Italic,
                    };

                internal static readonly GUIStyle PlaceHolder
                    = new(Type)
                    {
                        fontSize = 11,
                        alignment = TextAnchor.MiddleLeft,
                        contentOffset = new Vector2(2.5f, 0)
                    };
                internal static readonly GUIStyle FaintLinkLabel = new(PlaceHolder) { name = "Toggle", hover = { textColor = new Color(0.3f, 0.7f, 1) } };

                internal static readonly GUIStyle TypeFocused
                    = new(GUI.skin.label)
                    {
                        alignment = TextAnchor.MiddleRight,
                        normal =
                        {
                            textColor = EditorGUIUtility.isProSkin ? Color.white : Color.black,
                        },
                        fontStyle = FontStyle.Italic,
                    };

                internal static readonly GUIStyle TypeLabel = new(PlaceHolder) {contentOffset = new Vector2(-2.5f, 0)};
                internal static readonly GUIStyle RightPlaceHolder = new(TypeLabel) {alignment = TextAnchor.MiddleRight};
                internal static readonly GUIStyle Watermark
                    = new(PlaceHolder)
                    {
                        alignment = TextAnchor.MiddleRight,
                        fontSize  = 10,
                    };

                internal static readonly GUIStyle LabelDropdown
                    = new(GUI.skin.GetStyle("DropDownButton"))
                    {
                        alignment = TextAnchor.MiddleLeft,
                        contentOffset = new Vector2(2.5f, 0)
                    };

                internal static readonly GUIStyle RemoveIcon = new(GUI.skin.GetStyle("RL FooterButton"));

            }

            internal static readonly GUIStyle Icon = new(GUI.skin.label) {fixedWidth = 18, fixedHeight = 18};
            internal static readonly GUIStyle LetterButton = 
                new(GUI.skin.button) { padding = new RectOffset(), margin = new RectOffset(1,1,1,1), richText = true};

        }

        public static class Strings
        {
            public const string IconCopy = "SaveActive";
            public const string IconPaste = "Clipboard";
            public const string IconMove = "MoveTool";
            public const string IconPlace = "DefaultSorting";
            public const string IconDuplicate = "TreeEditor.Duplicate";
            public const string IconHelp = "_Help";
            public const string IconWarn = "console.warnicon.sml";
            public const string IconError = "console.erroricon.sml";
            public const string IconClear = "winbtn_win_close";
            public const string IconFolder = "FolderOpened Icon";
            public const string IconRemove = "Toolbar Minus";
            public const string IconSearch = "Search Icon";

            public const string ClipboardPrefixControl = "[TAG=VSP_CONTROL]";

            public const string SettingsCompact = "VSP_Compact";
        }

        public static class GUIContent
        {
            public const string MissingParametersTooltip = "No Expression Parameters targeted. Auto-fill and warnings are disabled.";
            public const string MenuFullTooltip = "Menu's controls are already maxed out. (8/8)";
            public static readonly UnityEngine.GUIContent Copy
                = new(EditorGUIUtility.IconContent(Strings.IconCopy))
                {
                    tooltip = "Copy"
                };

            public static readonly UnityEngine.GUIContent Paste
                = new(EditorGUIUtility.IconContent(Strings.IconPaste))
                {
                    tooltip = "Paste"
                };

            public static readonly UnityEngine.GUIContent Move
                = new(EditorGUIUtility.IconContent(Strings.IconMove))
                {
                    tooltip = "Move"
                };
            public static readonly UnityEngine.GUIContent Place
                = new(EditorGUIUtility.IconContent(Strings.IconPlace))
                {
                    tooltip = "Place"
                };

            public static readonly UnityEngine.GUIContent Duplicate
                = new(EditorGUIUtility.IconContent(Strings.IconDuplicate))
                {
                    tooltip = "Duplicate"
                };

            public static readonly UnityEngine.GUIContent Help = new(EditorGUIUtility.IconContent(Strings.IconHelp));

            public static readonly UnityEngine.GUIContent Warn = new(EditorGUIUtility.IconContent(Strings.IconWarn));
            public static readonly UnityEngine.GUIContent Error = new(EditorGUIUtility.IconContent(Strings.IconError));

            public static readonly UnityEngine.GUIContent Clear
                = new(EditorGUIUtility.IconContent(Strings.IconClear))
                {
                    tooltip = "Clear"
                };

            public static readonly UnityEngine.GUIContent Folder
                = new(EditorGUIUtility.IconContent(Strings.IconFolder))
                {
                    tooltip = "Open"
                };

            public static readonly UnityEngine.GUIContent Remove
                = new(EditorGUIUtility.IconContent(Strings.IconRemove)) {tooltip = "Remove element from list"};

            public static readonly UnityEngine.GUIContent Search
                = new(EditorGUIUtility.IconContent(Strings.IconSearch)) {tooltip = "Search"};
        }
        
        public static class Preferences
        {
            public static bool CompactMode
            {
                get => EditorPrefs.GetBool(Strings.SettingsCompact, false);
                set => EditorPrefs.SetBool(Strings.SettingsCompact, value);
            }
        }

        private static Color BrightnessToColor(float brightness)
        {
            if (brightness > 1) brightness /= 255;
            return new Color(brightness, brightness, brightness, 1);
        }
        private static readonly Texture2D TempTexture = new(1, 1) { anisoLevel = 0, filterMode = FilterMode.Point };

        private static Texture2D GetColorTexture(float rgb, float a = 1)
            => GetColorTexture(rgb, rgb, rgb, a);

        private static Texture2D GetColorTexture(float r, float g, float b, float a = 1)
        {
            if (r > 1) r /= 255;
            if (g > 1) g /= 255;
            if (b > 1) b /= 255;
            if (a > 1) a /= 255;

            return GetColorTexture(new Color(r, g, b, a));
        }

        private static Texture2D GetColorTexture(Color color)
        {
            TempTexture.SetPixel(0, 0, color);
            TempTexture.Apply();
            return TempTexture;
        }

        // ReSharper disable once InconsistentNaming  -Oh you use VS still? I much prefer rider
        public static VRCExpressionsMenu.Control.ControlType ToControlType(this SerializedProperty property)
        {
            var value = property.enumValueIndex;
            return value switch
            {
                0 => VRCExpressionsMenu.Control.ControlType.Button,
                1 => VRCExpressionsMenu.Control.ControlType.Toggle,
                2 => VRCExpressionsMenu.Control.ControlType.SubMenu,
                3 => VRCExpressionsMenu.Control.ControlType.TwoAxisPuppet,
                4 => VRCExpressionsMenu.Control.ControlType.FourAxisPuppet,
                5 => VRCExpressionsMenu.Control.ControlType.RadialPuppet,
                _ => VRCExpressionsMenu.Control.ControlType.Button
            };
        }

        public static int GetEnumValueIndex(this VRCExpressionsMenu.Control.ControlType type)
        {
            return type switch
            {
                VRCExpressionsMenu.Control.ControlType.Button => 0,
                VRCExpressionsMenu.Control.ControlType.Toggle => 1,
                VRCExpressionsMenu.Control.ControlType.SubMenu => 2,
                VRCExpressionsMenu.Control.ControlType.TwoAxisPuppet => 3,
                VRCExpressionsMenu.Control.ControlType.FourAxisPuppet => 4,
                VRCExpressionsMenu.Control.ControlType.RadialPuppet => 5,
                _ => -1
            };
        }

        public static int FindIndex(this IEnumerable array, object target)
        {
            var enumerator = array.GetEnumerator();
            using var enumerator1 = enumerator as IDisposable;
            var index = 0;
            while (enumerator.MoveNext())
            {
                if (enumerator.Current != null && enumerator.Current.Equals(target))
                    return index;
                index++;
            }

            return -1;
        }
        internal static bool GetPlayableLayer(this VRCAvatarDescriptor avi, VRCAvatarDescriptor.AnimLayerType type, out AnimatorController controller)
        {
            controller = (from l in avi.baseAnimationLayers.Concat(avi.specialAnimationLayers) where l.type == type select l.animatorController).FirstOrDefault() as AnimatorController;
            return controller != null;
        }

        internal static bool IterateArray(this SerializedProperty property, Func<int, SerializedProperty, bool> func, params int[] skipIndex)
        {
            for (var i = property.arraySize - 1; i >= 0; i--)
            {
                if (skipIndex.Contains(i)) continue;
                if (i >= property.arraySize) continue;
                if (func(i, property.GetArrayElementAtIndex(i)))
                    return true;
            }
            return false;
        }

        #region Keyboard Commands
        internal enum EventCommands
        {
            Copy,
            Cut,
            Paste,
            Duplicate,
            Delete,
            SoftDelete,
            SelectAll,
            Find,
            FrameSelected,
            FrameSelectedWithLock,
            FocusProjectWindow
        }
        internal static bool HasReceivedCommand(EventCommands command, string matchFocusControl = "", bool useEvent = true)
        {
            if (!string.IsNullOrEmpty(matchFocusControl) && GUI.GetNameOfFocusedControl() != matchFocusControl) return false;
            var e = Event.current;
            if (e.type != EventType.ValidateCommand) return false;
            var received = command.ToString() == e.commandName;
            if (received && useEvent) e.Use();
            return received;
        }

        private static bool HasReceivedKey(KeyCode key, string matchFocusControl = "", bool useEvent = true)
        {
            if (!string.IsNullOrEmpty(matchFocusControl) && GUI.GetNameOfFocusedControl() != matchFocusControl) return false;
            var e = Event.current;
            var received = e.type == EventType.KeyDown && e.keyCode == key;
            if (received && useEvent) e.Use();
            return received;
        }

        private static bool HasReceivedEnter(string matchFocusControl = "",bool useEvent = true) => HasReceivedKey(KeyCode.Return, matchFocusControl, useEvent) || HasReceivedKey(KeyCode.KeypadEnter, matchFocusControl, useEvent);
        private static bool HasReceivedCancel(string matchFocusControl = "",  bool useEvent = true) => HasReceivedKey(KeyCode.Escape, matchFocusControl, useEvent);
        internal static bool HasReceivedAnyDelete(string matchFocusControl = "", bool useEvent = true) => HasReceivedCommand(EventCommands.SoftDelete, matchFocusControl, useEvent) || HasReceivedCommand(EventCommands.Delete, matchFocusControl, useEvent) || HasReceivedKey(KeyCode.Delete, matchFocusControl, useEvent);
        private static bool HandleConfirmEvents(string matchFocusControl = "", Action onConfirm = null, Action onCancel = null)
        {
            if (HasReceivedEnter(matchFocusControl))
            {
                onConfirm?.Invoke();
                return true;
            }

            if (!HasReceivedCancel(matchFocusControl)) return false;
            onCancel?.Invoke();
            return true;
        }

        internal static void HandleTextFocusConfirmCommands(string matchFocusControl, Action onConfirm = null,
            Action onCancel = null)
        {
            if (!HandleConfirmEvents(matchFocusControl, onConfirm, onCancel)) return;
            GUI.FocusControl(null);
        }
        #endregion

        internal abstract class CustomDropdownBase : PopupWindowContent
        {
            internal static readonly GUIStyle BackgroundStyle = new()
            {
                hover = { background = GetColorTexture(new Color(0.3020f, 0.3020f, 0.3020f)) },
                active = { background = GetColorTexture(new Color(0.1725f, 0.3647f, 0.5294f)) }
            };

            internal static readonly GUIStyle TitleStyle = new(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold
            };

        }
        internal class CustomDropdown<T> : CustomDropdownBase
        {

            private readonly string _title;
            private string _search;
            private DropDownItem[] Items;
            private readonly Action<DropDownItem> _itemGUI;
            private readonly Action<int, T> _onSelected;
            private Func<T, string, bool> _onSearchChanged;

            private bool _hasSearch;
            private float _width;
            private bool _firstPass = true;
            private Vector2 _scroll;
            private readonly Rect[] _selectionRects;

            public CustomDropdown(string title, IEnumerable<T> itemArray, Action<DropDownItem> itemGUI, Action<int, T> onSelected)
            {
                _title = title;
                _onSelected = onSelected;
                _itemGUI = itemGUI;
                Items = itemArray.Select((item, i) => new DropDownItem(item, i)).ToArray();
                _selectionRects = new Rect[Items.Length];
            }

            public void EnableSearch(Func<T, string, bool> onSearchChanged)
            {
                _hasSearch = true;
                _onSearchChanged = onSearchChanged;
            }

            public override void OnGUI(Rect rect)
            {

                using (new GUILayout.AreaScope(rect))
                {
                    var e = Event.current;
                    _scroll = GUILayout.BeginScrollView(_scroll);
                    if (!string.IsNullOrEmpty(_title))
                    {
                        GUILayout.Label(_title, TitleStyle);
                        DrawSeparator();
                    }
                    if (_hasSearch)
                    {
                        EditorGUI.BeginChangeCheck();
                        if (_firstPass) GUI.SetNextControlName($"{_title}SearchBar");
                        _search = EditorGUILayout.TextField(_search, GUI.skin.GetStyle("SearchTextField"));
                        if (EditorGUI.EndChangeCheck())
                        {
                            foreach (var i in Items)
                                i.Displayed = _onSearchChanged(i.Value, _search);
                        }
                    }

                    var t = e.type;
                    for (var i = 0; i < Items.Length; i++)
                    {
                        var item = Items[i];
                        if (!item.Displayed) continue;
                        if (!_firstPass)
                        {
                            if (GUI.Button(_selectionRects[i], string.Empty, BackgroundStyle))
                            {
                                _onSelected(item.ItemIndex, item.Value);
                                editorWindow.Close();
                            }
                        }
                        using (new GUILayout.VerticalScope()) _itemGUI(item);

                        if (t != EventType.Repaint) continue;
                        _selectionRects[i] = GUILayoutUtility.GetLastRect();

                        if (_firstPass && _selectionRects[i].width > _width)
                            _width = _selectionRects[i].width;
                    }

                    if (t == EventType.Repaint && _firstPass)
                    {
                        _firstPass = false;
                        GUI.FocusControl($"{_title}SearchBar");
                    }
                    GUILayout.EndScrollView();
                    if (rect.Contains(e.mousePosition))
                        editorWindow.Repaint();
                }
            }

            public override Vector2 GetWindowSize()
            {
                var ogSize = base.GetWindowSize();
                if (!_firstPass) ogSize.x = _width + 21;
                return ogSize;
            }

            public void Show(Rect position) => PopupWindow.Show(position, this);
            internal class DropDownItem
            {
                internal readonly int ItemIndex;
                internal readonly T Value;

                private readonly object[] _args;
                internal bool Displayed = true;

                internal object Extra
                {
                    get => _args[0];
                    set => _args[0] = value;
                }

                internal DropDownItem(T value, int itemIndex, object[] args)
                {
                    Value = value;
                    ItemIndex = itemIndex;
                    _args = args;
                }

                public DropDownItem(T value, int itemIndex)
                {
                    throw new NotImplementedException();
                }

                public static implicit operator T(DropDownItem i) => i.Value;
            }

            private static void DrawSeparator(int thickness = 2, int padding = 10)
            {
                var r = EditorGUILayout.GetControlRect(GUILayout.Height(thickness + padding));
                r.height = thickness;
                r.y += padding / 2f;
                r.x -= 2;
                r.width += 6;
                ColorUtility.TryParseHtmlString(EditorGUIUtility.isProSkin ? "#595959" : "#858585", out var lineColor);
                EditorGUI.DrawRect(r, lineColor);
            }

        }

    }



}