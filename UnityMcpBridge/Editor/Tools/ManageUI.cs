using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Runtime.UI;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>
    /// High level helpers for creating and configuring Unity UI (UGUI) content at edit-time.
    /// Focuses on beginner-friendly operations exposed through the MCP bridge.
    /// </summary>
    [McpForUnityTool("manage_ui")]
    public static class ManageUI
    {
        private const string DefaultCanvasName = "MCP Canvas";

        private static readonly List<ActiveAnimation> ActiveAnimations = new List<ActiveAnimation>();
        private static bool animationHookRegistered;

        // --- Entry Point ---

        public static object HandleCommand(JObject @params)
        {
            if (@params == null)
            {
                return Response.Error("Parameters cannot be null.");
            }

            string action = @params["action"]?.ToString()?.ToLowerInvariant();
            if (string.IsNullOrEmpty(action))
            {
                return Response.Error("'action' parameter is required.");
            }

            try
            {
                switch (action)
                {
                    case "create_element":
                        return CreateElement(@params);
                    case "set_properties":
                        return SetElementProperties(@params);
                    case "set_parent":
                        return SetParent(@params);
                    case "set_sibling_index":
                        return SetSiblingIndex(@params);
                    case "create_canvas":
                        return CreateCanvas(@params);
                    case "delete_canvas":
                        return DeleteCanvas(@params);
                    case "set_canvas_properties":
                        return SetCanvasProperties(@params);
                    case "bind_event":
                        return BindEvent(@params);
                    case "set_interaction_state":
                        return SetInteractionState(@params);
                    case "set_visibility":
                        return SetVisibility(@params);
                    case "animate":
                        return Animate(@params);
                    default:
                        return Response.Error($"Unknown UI action '{action}'.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ManageUI] Action '{action}' failed: {ex}");
                return Response.Error($"Internal error processing '{action}': {ex.Message}");
            }
        }

        // --- Actions ---

        private static object CreateElement(JObject @params)
        {
            string elementType = @params["elementType"]?.ToString()?.ToLowerInvariant();
            if (string.IsNullOrEmpty(elementType))
            {
                return Response.Error("'elementType' parameter is required (button, text, image, slider, toggle, panel).");
            }

            string name = @params["name"]?.ToString();
            GameObject parent = FindTarget(@params["parent"]);
            if (parent != null && parent.transform is not RectTransform)
            {
                return Response.Error("UI parent must use a RectTransform.");
            }

            GameObject canvas = parent ?? EnsureCanvas(@params["canvas"] as JObject);

            GameObject newElement;
            switch (elementType)
            {
                case "button":
                    newElement = CreateButton(name, canvas.transform);
                    break;
                case "text":
                    newElement = CreateText(name, canvas.transform);
                    break;
                case "image":
                    newElement = CreateImage(name, canvas.transform);
                    break;
                case "slider":
                    newElement = CreateSlider(name, canvas.transform);
                    break;
                case "toggle":
                    newElement = CreateToggle(name, canvas.transform);
                    break;
                case "panel":
                case "container":
                    newElement = CreatePanel(name, canvas.transform);
                    break;
                default:
                    return Response.Error($"Unsupported element type '{elementType}'.");
            }

            if (parent != null)
            {
                newElement.transform.SetParent(parent.transform, false);
            }

            ApplyRectTransform(newElement.GetComponent<RectTransform>(), @params["rectTransform"] as JObject);
            ApplyElementProperties(newElement, @params["properties"] as JObject);

            EnsureEventSystem();

            EditorUtility.SetDirty(newElement);
            EditorSceneManager.MarkSceneDirty(newElement.scene);

            return Response.Success(
                $"Created UI {elementType} '{newElement.name}'.",
                GameObjectSerializer.GetGameObjectData(newElement)
            );
        }

        private static object SetElementProperties(JObject @params)
        {
            GameObject target = FindTarget(@params["target"]);
            if (target == null)
            {
                return Response.Error("Target UI element not found.");
            }

            ApplyRectTransform(target.GetComponent<RectTransform>(), @params["rectTransform"] as JObject);
            ApplyElementProperties(target, @params["properties"] as JObject);

            EditorUtility.SetDirty(target);
            EditorSceneManager.MarkSceneDirty(target.scene);

            return Response.Success(
                $"Updated UI element '{target.name}'.",
                GameObjectSerializer.GetGameObjectData(target)
            );
        }

        private static object SetParent(JObject @params)
        {
            GameObject target = FindTarget(@params["target"]);
            if (target == null)
            {
                return Response.Error("Target UI element not found.");
            }

            GameObject parent = FindTarget(@params["parent"]);
            if (parent == null)
            {
                return Response.Error("Parent UI element not found.");
            }

            RectTransform targetRect = target.GetComponent<RectTransform>();
            RectTransform parentRect = parent.GetComponent<RectTransform>();
            if (targetRect == null || parentRect == null)
            {
                return Response.Error("Both target and parent must have RectTransform components.");
            }

            Undo.RegisterCompleteObjectUndo(targetRect, "Set UI Parent");
            targetRect.SetParent(parentRect, false);
            EditorUtility.SetDirty(targetRect);
            EditorSceneManager.MarkSceneDirty(target.scene);

            return Response.Success(
                $"Parent for '{target.name}' set to '{parent.name}'.",
                GameObjectSerializer.GetGameObjectData(target)
            );
        }

        private static object SetSiblingIndex(JObject @params)
        {
            GameObject target = FindTarget(@params["target"]);
            if (target == null)
            {
                return Response.Error("Target UI element not found.");
            }

            if (!int.TryParse(@params["index"]?.ToString(), out int index))
            {
                return Response.Error("'index' parameter must be a valid integer.");
            }

            RectTransform rect = target.GetComponent<RectTransform>();
            if (rect == null)
            {
                return Response.Error("Target must have a RectTransform.");
            }

            Undo.RegisterCompleteObjectUndo(rect, "Set UI Sibling Index");
            rect.SetSiblingIndex(index);
            EditorUtility.SetDirty(rect);
            EditorSceneManager.MarkSceneDirty(target.scene);

            return Response.Success(
                $"Set sibling index of '{target.name}' to {index}.",
                GameObjectSerializer.GetGameObjectData(target)
            );
        }

        private static object CreateCanvas(JObject @params)
        {
            GameObject canvasGo = CreateCanvasInternal(@params);
            EnsureEventSystem();

            return Response.Success(
                $"Created canvas '{canvasGo.name}'.",
                GameObjectSerializer.GetGameObjectData(canvasGo)
            );
        }

        private static object DeleteCanvas(JObject @params)
        {
            GameObject canvas = FindTarget(@params["target"]);
            if (canvas == null)
            {
                return Response.Error("Canvas not found.");
            }

            if (canvas.GetComponent<Canvas>() == null)
            {
                return Response.Error("Target is not a Canvas.");
            }

            Undo.DestroyObjectImmediate(canvas);
            return Response.Success($"Deleted canvas '{canvas.name}'.");
        }

        private static object SetCanvasProperties(JObject @params)
        {
            GameObject canvasGo = FindTarget(@params["target"]);
            if (canvasGo == null)
            {
                return Response.Error("Canvas not found.");
            }

            Canvas canvas = canvasGo.GetComponent<Canvas>();
            if (canvas == null)
            {
                return Response.Error("Target is not a Canvas.");
            }

            Undo.RegisterCompleteObjectUndo(canvas, "Set Canvas Properties");
            JObject props = @params["properties"] as JObject;
            if (props != null)
            {
                if (props.TryGetValue("renderMode", out JToken renderModeToken) && Enum.TryParse(renderModeToken.ToString(), true, out RenderMode renderMode))
                {
                    canvas.renderMode = renderMode;
                }

                if (props.TryGetValue("sortOrder", out JToken sortOrderToken) && int.TryParse(sortOrderToken.ToString(), out int sortOrder))
                {
                    canvas.sortingOrder = sortOrder;
                }

                if (props.TryGetValue("pixelPerfect", out JToken pixelPerfectToken) && bool.TryParse(pixelPerfectToken.ToString(), out bool pixelPerfect))
                {
                    canvas.pixelPerfect = pixelPerfect;
                }
            }

            CanvasScaler scaler = canvasGo.GetComponent<CanvasScaler>();
            if (scaler != null && props != null && props.TryGetValue("scaler", out JToken scalerToken) && scalerToken is JObject scalerProps)
            {
                Undo.RegisterCompleteObjectUndo(scaler, "Set Canvas Scaler Properties");
                if (scalerProps.TryGetValue("uiScaleMode", out JToken modeToken) && Enum.TryParse(modeToken.ToString(), true, out CanvasScaler.ScaleMode mode))
                {
                    scaler.uiScaleMode = mode;
                }
                if (scalerProps.TryGetValue("referenceResolution", out JToken resToken) && ParseVector2(resToken, out Vector2 resolution))
                {
                    scaler.referenceResolution = resolution;
                }
                if (scalerProps.TryGetValue("matchWidthOrHeight", out JToken matchToken) && float.TryParse(matchToken.ToString(), out float match))
                {
                    scaler.matchWidthOrHeight = Mathf.Clamp01(match);
                }
            }

            EditorUtility.SetDirty(canvas);
            EditorSceneManager.MarkSceneDirty(canvasGo.scene);

            return Response.Success(
                $"Updated canvas '{canvasGo.name}'.",
                GameObjectSerializer.GetGameObjectData(canvasGo)
            );
        }

        private static object BindEvent(JObject @params)
        {
            GameObject target = FindTarget(@params["target"]);
            if (target == null)
            {
                return Response.Error("Target UI element not found.");
            }

            string eventType = @params["eventType"]?.ToString()?.ToLowerInvariant();
            if (string.IsNullOrEmpty(eventType))
            {
                return Response.Error("'eventType' parameter is required (e.g., onClick, onValueChanged).");
            }

            GameObject handlerObject = FindTarget(@params["handlerTarget"] ?? @params["handlerObject"]);
            if (handlerObject == null)
            {
                handlerObject = target;
            }

            string componentName = @params["handlerComponent"]?.ToString();
            string methodName = @params["handlerMethod"]?.ToString();
            if (string.IsNullOrEmpty(methodName))
            {
                return Response.Error("'handlerMethod' parameter is required.");
            }

            Component component = null;
            if (!string.IsNullOrEmpty(componentName))
            {
                component = handlerObject.GetComponent(componentName) ?? handlerObject.GetComponents<Component>().FirstOrDefault(c => c.GetType().FullName == componentName || c.GetType().Name == componentName);
            }

            if (component == null)
            {
                component = handlerObject.GetComponent<MonoBehaviour>();
            }

            if (component == null)
            {
                return Response.Error("Could not locate component to bind method on.");
            }

            UIEventProxy proxy = target.GetComponent<UIEventProxy>();
            if (proxy == null)
            {
                Undo.AddComponent<UIEventProxy>(target);
                proxy = target.GetComponent<UIEventProxy>();
            }

            proxy.Target = component;
            proxy.MethodName = methodName;
            proxy.Argument = @params["argument"]?.ToString();

            bool append = @params["append"]?.ToObject<bool>() ?? false;
            switch (eventType)
            {
                case "onclick":
                    Button button = target.GetComponent<Button>();
                    if (button == null)
                    {
                        return Response.Error("Target does not have a Button component for onClick binding.");
                    }
                    if (!append)
                    {
                        ClearPersistentListeners(button.onClick);
                        button.onClick.RemoveAllListeners();
                    }
                    UnityEventTools.AddPersistentListener(button.onClick, proxy.Invoke);
                    EditorUtility.SetDirty(button);
                    break;

                case "onvaluechanged":
                    Slider slider = target.GetComponent<Slider>();
                    if (slider != null)
                    {
                        if (!append)
                        {
                            ClearPersistentListeners(slider.onValueChanged);
                            slider.onValueChanged.RemoveAllListeners();
                        }
                        UnityEventTools.AddPersistentListener(slider.onValueChanged, proxy.InvokeFloat);
                        break;
                    }

                    Toggle toggle = target.GetComponent<Toggle>();
                    if (toggle != null)
                    {
                        if (!append)
                        {
                            ClearPersistentListeners(toggle.onValueChanged);
                            toggle.onValueChanged.RemoveAllListeners();
                        }
                        UnityEventTools.AddPersistentListener(toggle.onValueChanged, proxy.InvokeBool);
                        break;
                    }

                    return Response.Error("Target does not expose an onValueChanged UnityEvent.");

                default:
                    return Response.Error($"Unsupported event type '{eventType}'.");
            }

            EditorUtility.SetDirty(proxy);
            EditorSceneManager.MarkSceneDirty(target.scene);

            return Response.Success(
                $"Bound {eventType} on '{target.name}' to '{component.GetType().Name}.{methodName}'.",
                GameObjectSerializer.GetGameObjectData(target)
            );
        }

        private static object SetInteractionState(JObject @params)
        {
            GameObject target = FindTarget(@params["target"]);
            if (target == null)
            {
                return Response.Error("Target UI element not found.");
            }

            if (!bool.TryParse(@params["interactable"]?.ToString(), out bool interactable))
            {
                return Response.Error("'interactable' parameter must be true or false.");
            }

            Selectable selectable = target.GetComponent<Selectable>();
            if (selectable == null)
            {
                return Response.Error("Target does not have a Selectable component.");
            }

            Undo.RegisterCompleteObjectUndo(selectable, "Set UI Interactable");
            selectable.interactable = interactable;
            EditorUtility.SetDirty(selectable);
            EditorSceneManager.MarkSceneDirty(target.scene);

            return Response.Success(
                $"Set interactable={interactable} on '{target.name}'.",
                GameObjectSerializer.GetGameObjectData(target)
            );
        }

        private static object SetVisibility(JObject @params)
        {
            GameObject target = FindTarget(@params["target"]);
            if (target == null)
            {
                return Response.Error("Target UI element not found.");
            }

            if (!bool.TryParse(@params["visible"]?.ToString(), out bool visible))
            {
                return Response.Error("'visible' parameter must be true or false.");
            }

            Undo.RegisterCompleteObjectUndo(target, "Set UI Visibility");
            target.SetActive(visible);
            EditorUtility.SetDirty(target);
            EditorSceneManager.MarkSceneDirty(target.scene);

            return Response.Success(
                $"Set visibility of '{target.name}' to {visible}.",
                GameObjectSerializer.GetGameObjectData(target)
            );
        }

        private static object Animate(JObject @params)
        {
            GameObject target = FindTarget(@params["target"]);
            if (target == null)
            {
                return Response.Error("Target UI element not found.");
            }

            string animationType = @params["animation"]?.ToString()?.ToLowerInvariant();
            if (string.IsNullOrEmpty(animationType))
            {
                return Response.Error("'animation' parameter is required (fade, slide, scale).");
            }

            if (!float.TryParse(@params["duration"]?.ToString(), out float duration) || duration < 0f)
            {
                duration = 0.25f;
            }

            ActiveAnimation animation = new ActiveAnimation
            {
                Target = target,
                Mode = animationType,
                Duration = Mathf.Max(0.0001f, duration),
                StartTime = EditorApplication.timeSinceStartup
            };

            RectTransform rect = target.GetComponent<RectTransform>();
            switch (animationType)
            {
                case "fade":
                    CanvasGroup canvasGroup = target.GetComponent<CanvasGroup>() ?? Undo.AddComponent<CanvasGroup>(target);
                    animation.CanvasGroup = canvasGroup;
                    animation.From = new Vector3(canvasGroup.alpha, 0f, 0f);
                    if (!ParseFloat(@params["from"], out float fadeFrom))
                    {
                        fadeFrom = canvasGroup.alpha;
                    }
                    if (!ParseFloat(@params["to"], out float fadeTo))
                    {
                        fadeTo = fadeFrom == 1f ? 0f : 1f;
                    }
                    canvasGroup.alpha = fadeFrom;
                    animation.From = new Vector3(fadeFrom, 0f, 0f);
                    animation.To = new Vector3(fadeTo, 0f, 0f);
                    break;

                case "slide":
                    if (rect == null)
                    {
                        return Response.Error("Slide animation requires RectTransform.");
                    }
                    animation.RectTransform = rect;
                    animation.From = rect.anchoredPosition3D;
                    if (ParseVector3(@params["from"], out Vector3 slideFrom))
                    {
                        rect.anchoredPosition3D = slideFrom;
                        animation.From = slideFrom;
                    }
                    if (!ParseVector3(@params["to"], out Vector3 slideTo))
                    {
                        return Response.Error("Slide animation requires a 'to' vector.");
                    }
                    animation.To = slideTo;
                    break;

                case "scale":
                    if (rect == null)
                    {
                        rect = target.transform as RectTransform;
                    }
                    animation.RectTransform = rect;
                    animation.From = target.transform.localScale;
                    if (ParseVector3(@params["from"], out Vector3 scaleFrom))
                    {
                        target.transform.localScale = scaleFrom;
                        animation.From = scaleFrom;
                    }
                    if (!ParseVector3(@params["to"], out Vector3 scaleTo))
                    {
                        return Response.Error("Scale animation requires a 'to' vector.");
                    }
                    animation.To = scaleTo;
                    break;

                default:
                    return Response.Error($"Unsupported animation type '{animationType}'.");
            }

            RegisterAnimation(animation);
            return Response.Success($"Started {animationType} animation on '{target.name}'.");
        }

        // --- Animation bookkeeping ---

        private static void RegisterAnimation(ActiveAnimation animation)
        {
            ActiveAnimations.Add(animation);
            if (!animationHookRegistered)
            {
                EditorApplication.update += OnEditorUpdate;
                animationHookRegistered = true;
            }
        }

        private static void OnEditorUpdate()
        {
            if (ActiveAnimations.Count == 0)
            {
                if (animationHookRegistered)
                {
                    EditorApplication.update -= OnEditorUpdate;
                    animationHookRegistered = false;
                }
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            for (int i = ActiveAnimations.Count - 1; i >= 0; i--)
            {
                ActiveAnimation animation = ActiveAnimations[i];
                if (animation.Target == null)
                {
                    ActiveAnimations.RemoveAt(i);
                    continue;
                }

                float t = Mathf.Clamp01((float)((now - animation.StartTime) / animation.Duration));
                switch (animation.Mode)
                {
                    case "fade":
                        if (animation.CanvasGroup != null)
                        {
                            animation.CanvasGroup.alpha = Mathf.Lerp(animation.From.x, animation.To.x, t);
                            EditorUtility.SetDirty(animation.CanvasGroup);
                        }
                        break;
                    case "slide":
                        if (animation.RectTransform != null)
                        {
                            animation.RectTransform.anchoredPosition3D = Vector3.Lerp(animation.From, animation.To, t);
                            EditorUtility.SetDirty(animation.RectTransform);
                        }
                        break;
                    case "scale":
                        animation.Target.transform.localScale = Vector3.Lerp(animation.From, animation.To, t);
                        EditorUtility.SetDirty(animation.Target.transform);
                        break;
                }

                if (t >= 0.999f)
                {
                    ActiveAnimations.RemoveAt(i);
                }
            }
        }

        // --- Creation helpers ---

        private static GameObject CreateButton(string name, Transform canvasTransform)
        {
            GameObject buttonRoot = CreateUIElementRoot(name ?? "Button", canvasTransform, new Vector2(160, 40));
            Image image = buttonRoot.AddComponent<Image>();
            image.color = new Color(0.82f, 0.82f, 0.82f, 1f);
            Button button = buttonRoot.AddComponent<Button>();
            button.targetGraphic = image;

            GameObject textGo = CreateUIObject("Text", buttonRoot.transform);
            Text text = textGo.AddComponent<Text>();
            text.text = string.IsNullOrEmpty(name) ? "Button" : name;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.black;
            text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            RectTransform textRect = text.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            return buttonRoot;
        }

        private static GameObject CreateText(string name, Transform canvasTransform)
        {
            GameObject textRoot = CreateUIElementRoot(name ?? "Text", canvasTransform, new Vector2(160, 30));
            Text text = textRoot.AddComponent<Text>();
            text.text = string.IsNullOrEmpty(name) ? "New Text" : name;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.black;
            text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            return textRoot;
        }

        private static GameObject CreateImage(string name, Transform canvasTransform)
        {
            GameObject imageRoot = CreateUIElementRoot(name ?? "Image", canvasTransform, new Vector2(100, 100));
            Image image = imageRoot.AddComponent<Image>();
            image.color = Color.white;
            return imageRoot;
        }

        private static GameObject CreatePanel(string name, Transform canvasTransform)
        {
            GameObject panelRoot = CreateUIElementRoot(name ?? "Panel", canvasTransform, new Vector2(200, 200));
            Image image = panelRoot.AddComponent<Image>();
            image.color = new Color(1f, 1f, 1f, 0.6f);
            return panelRoot;
        }

        private static GameObject CreateSlider(string name, Transform canvasTransform)
        {
            GameObject sliderRoot = CreateUIElementRoot(name ?? "Slider", canvasTransform, new Vector2(160, 20));
            Slider slider = sliderRoot.AddComponent<Slider>();

            GameObject background = CreateUIObject("Background", sliderRoot.transform);
            Image bgImage = background.AddComponent<Image>();
            bgImage.sprite = Resources.GetBuiltinResource<Sprite>("UI/Skin/Background.psd");
            bgImage.type = Image.Type.Sliced;
            RectTransform bgRect = background.GetComponent<RectTransform>();
            bgRect.anchorMin = new Vector2(0f, 0.25f);
            bgRect.anchorMax = new Vector2(1f, 0.75f);
            bgRect.offsetMin = bgRect.offsetMax = Vector2.zero;

            GameObject fillArea = CreateUIObject("Fill Area", sliderRoot.transform);
            RectTransform fillAreaRect = fillArea.GetComponent<RectTransform>();
            fillAreaRect.anchorMin = new Vector2(0f, 0.25f);
            fillAreaRect.anchorMax = new Vector2(1f, 0.75f);
            fillAreaRect.offsetMin = new Vector2(10f, 0f);
            fillAreaRect.offsetMax = new Vector2(-10f, 0f);

            GameObject fill = CreateUIObject("Fill", fillArea.transform);
            Image fillImage = fill.AddComponent<Image>();
            fillImage.sprite = Resources.GetBuiltinResource<Sprite>("UI/Skin/UISprite.psd");
            fillImage.type = Image.Type.Sliced;
            RectTransform fillRect = fill.GetComponent<RectTransform>();
            fillRect.anchorMin = new Vector2(0f, 0f);
            fillRect.anchorMax = new Vector2(1f, 1f);
            fillRect.offsetMin = fillRect.offsetMax = Vector2.zero;

            GameObject handleSlideArea = CreateUIObject("Handle Slide Area", sliderRoot.transform);
            RectTransform handleAreaRect = handleSlideArea.GetComponent<RectTransform>();
            handleAreaRect.anchorMin = new Vector2(0f, 0f);
            handleAreaRect.anchorMax = new Vector2(1f, 1f);
            handleAreaRect.offsetMin = new Vector2(10f, -10f);
            handleAreaRect.offsetMax = new Vector2(-10f, 10f);

            GameObject handle = CreateUIObject("Handle", handleSlideArea.transform);
            Image handleImage = handle.AddComponent<Image>();
            handleImage.sprite = Resources.GetBuiltinResource<Sprite>("UI/Skin/Knob.psd");
            RectTransform handleRect = handle.GetComponent<RectTransform>();
            handleRect.sizeDelta = new Vector2(20f, 20f);

            slider.fillRect = fillRect;
            slider.handleRect = handleRect;
            slider.targetGraphic = handleImage;

            return sliderRoot;
        }

        private static GameObject CreateToggle(string name, Transform canvasTransform)
        {
            GameObject toggleRoot = CreateUIElementRoot(name ?? "Toggle", canvasTransform, new Vector2(160, 20));
            Toggle toggle = toggleRoot.AddComponent<Toggle>();

            GameObject background = CreateUIObject("Background", toggleRoot.transform);
            Image bgImage = background.AddComponent<Image>();
            bgImage.sprite = Resources.GetBuiltinResource<Sprite>("UI/Skin/Background.psd");
            bgImage.type = Image.Type.Sliced;
            RectTransform bgRect = background.GetComponent<RectTransform>();
            bgRect.anchorMin = new Vector2(0f, 0.5f);
            bgRect.anchorMax = new Vector2(0f, 0.5f);
            bgRect.sizeDelta = new Vector2(20f, 20f);

            GameObject checkmark = CreateUIObject("Checkmark", background.transform);
            Image checkmarkImage = checkmark.AddComponent<Image>();
            checkmarkImage.sprite = Resources.GetBuiltinResource<Sprite>("UI/Skin/Checkmark.psd");
            RectTransform checkRect = checkmark.GetComponent<RectTransform>();
            checkRect.anchorMin = new Vector2(0.5f, 0.5f);
            checkRect.anchorMax = new Vector2(0.5f, 0.5f);
            checkRect.sizeDelta = new Vector2(20f, 20f);

            GameObject label = CreateUIObject("Label", toggleRoot.transform);
            Text labelText = label.AddComponent<Text>();
            labelText.text = string.IsNullOrEmpty(name) ? "Toggle" : name;
            labelText.alignment = TextAnchor.MiddleLeft;
            labelText.color = Color.black;
            labelText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            RectTransform labelRect = label.GetComponent<RectTransform>();
            labelRect.anchorMin = new Vector2(0f, 0f);
            labelRect.anchorMax = new Vector2(1f, 1f);
            labelRect.offsetMin = new Vector2(25f, 0f);
            labelRect.offsetMax = new Vector2(0f, 0f);

            toggle.graphic = checkmarkImage;
            toggle.targetGraphic = bgImage;

            return toggleRoot;
        }

        private static GameObject CreateUIElementRoot(string name, Transform parent, Vector2 size)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            Undo.RegisterCreatedObjectUndo(go, $"Create UI Element {name}");
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = size;
            if (parent != null)
            {
                SetParentAndAlign(rect, parent as RectTransform);
            }
            return go;
        }

        private static GameObject CreateUIObject(string name, Transform parent)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            Undo.RegisterCreatedObjectUndo(go, $"Create UI Element {name}");
            SetParentAndAlign(go.transform as RectTransform, parent as RectTransform);
            return go;
        }

        private static void SetParentAndAlign(RectTransform rect, RectTransform parent)
        {
            if (rect == null || parent == null) return;
            rect.SetParent(parent, false);
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.localScale = Vector3.one;
        }

        private static GameObject EnsureCanvas(JObject canvasParams)
        {
            string requestedName = canvasParams?["name"]?.ToString();
            if (!string.IsNullOrEmpty(requestedName))
            {
                GameObject namedCanvas = FindTarget(new JValue(requestedName));
                if (namedCanvas != null && namedCanvas.GetComponent<Canvas>() != null)
                {
                    EnsureEventSystem();
                    return namedCanvas;
                }
            }

            Canvas existing = GameObject.FindObjectOfType<Canvas>();
            if (existing != null)
            {
                EnsureEventSystem();
                return existing.gameObject;
            }

            GameObject canvas = CreateCanvasInternal(canvasParams);
            EnsureEventSystem();
            return canvas;
        }

        private static GameObject CreateCanvasInternal(JObject canvasParams)
        {
            string name = canvasParams?["name"]?.ToString();
            if (string.IsNullOrEmpty(name))
            {
                name = DefaultCanvasName;
            }

            GameObject go = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            Undo.RegisterCreatedObjectUndo(go, $"Create Canvas {name}");
            Canvas canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.pixelPerfect = true;

            CanvasScaler scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            GraphicRaycaster raycaster = go.GetComponent<GraphicRaycaster>();
            raycaster.ignoreReversedGraphics = true;

            if (canvasParams != null)
            {
                JObject properties = canvasParams["properties"] as JObject;
                if (properties != null)
                {
                    if (properties.TryGetValue("renderMode", out JToken renderModeToken) && Enum.TryParse(renderModeToken.ToString(), true, out RenderMode renderMode))
                    {
                        canvas.renderMode = renderMode;
                    }
                    if (properties.TryGetValue("sortOrder", out JToken sortOrderToken) && int.TryParse(sortOrderToken.ToString(), out int sortOrder))
                    {
                        canvas.sortingOrder = sortOrder;
                    }
                    if (properties.TryGetValue("pixelPerfect", out JToken pixelPerfectToken) && bool.TryParse(pixelPerfectToken.ToString(), out bool pixelPerfect))
                    {
                        canvas.pixelPerfect = pixelPerfect;
                    }
                }
            }

            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            return go;
        }

        private static void EnsureEventSystem()
        {
            EventSystem eventSystem = GameObject.FindObjectOfType<EventSystem>();
            if (eventSystem != null) return;

            GameObject esGo = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            Undo.RegisterCreatedObjectUndo(esGo, "Create EventSystem");
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        }

        // --- Property helpers ---

        private static void ApplyRectTransform(RectTransform rect, JObject props)
        {
            if (rect == null || props == null) return;

            if (props.TryGetValue("anchoredPosition", out JToken anchoredPositionToken) && ParseVector2(anchoredPositionToken, out Vector2 anchored))
            {
                rect.anchoredPosition = anchored;
            }
            if (props.TryGetValue("anchoredPosition3D", out JToken anchored3dToken) && ParseVector3(anchored3dToken, out Vector3 anchored3D))
            {
                rect.anchoredPosition3D = anchored3D;
            }
            if (props.TryGetValue("sizeDelta", out JToken sizeDeltaToken) && ParseVector2(sizeDeltaToken, out Vector2 sizeDelta))
            {
                rect.sizeDelta = sizeDelta;
            }
            if (props.TryGetValue("anchorMin", out JToken anchorMinToken) && ParseVector2(anchorMinToken, out Vector2 anchorMin))
            {
                rect.anchorMin = anchorMin;
            }
            if (props.TryGetValue("anchorMax", out JToken anchorMaxToken) && ParseVector2(anchorMaxToken, out Vector2 anchorMax))
            {
                rect.anchorMax = anchorMax;
            }
            if (props.TryGetValue("pivot", out JToken pivotToken) && ParseVector2(pivotToken, out Vector2 pivot))
            {
                rect.pivot = pivot;
            }
            if (props.TryGetValue("rotation", out JToken rotationToken) && ParseVector3(rotationToken, out Vector3 rotation))
            {
                rect.localEulerAngles = rotation;
            }
            if (props.TryGetValue("scale", out JToken scaleToken) && ParseVector3(scaleToken, out Vector3 scale))
            {
                rect.localScale = scale;
            }
        }

        private static void ApplyElementProperties(GameObject target, JObject props)
        {
            if (target == null || props == null) return;

            Graphic graphic = target.GetComponent<Graphic>();
            if (props.TryGetValue("color", out JToken colorToken) && ParseColor(colorToken, out Color color))
            {
                if (graphic != null)
                {
                    Undo.RegisterCompleteObjectUndo(graphic, "Set UI Color");
                    graphic.color = color;
                    EditorUtility.SetDirty(graphic);
                }
            }

            if (props.TryGetValue("text", out JToken textToken))
            {
                Text text = target.GetComponent<Text>() ?? target.GetComponentInChildren<Text>();
                if (text != null)
                {
                    Undo.RegisterCompleteObjectUndo(text, "Set UI Text");
                    text.text = textToken.ToString();
                    EditorUtility.SetDirty(text);
                }
            }

            if (props.TryGetValue("image", out JToken imageToken))
            {
                Image image = target.GetComponent<Image>();
                if (image != null && imageToken is JObject imageProps)
                {
                    Undo.RegisterCompleteObjectUndo(image, "Set UI Image Properties");
                    if (imageProps.TryGetValue("type", out JToken typeToken) && Enum.TryParse(typeToken.ToString(), true, out Image.Type type))
                    {
                        image.type = type;
                    }
                    if (imageProps.TryGetValue("preserveAspect", out JToken preserveToken) && bool.TryParse(preserveToken.ToString(), out bool preserve))
                    {
                        image.preserveAspect = preserve;
                    }
                    if (imageProps.TryGetValue("sprite", out JToken spriteToken))
                    {
                        string path = spriteToken.ToString();
                        Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
                        if (sprite != null)
                        {
                            image.sprite = sprite;
                        }
                    }
                    EditorUtility.SetDirty(image);
                }
            }

            if (props.TryGetValue("alignment", out JToken alignmentToken))
            {
                Text text = target.GetComponent<Text>();
                if (text != null && Enum.TryParse(alignmentToken.ToString(), true, out TextAnchor anchor))
                {
                    Undo.RegisterCompleteObjectUndo(text, "Set UI Text Alignment");
                    text.alignment = anchor;
                    EditorUtility.SetDirty(text);
                }
            }

            if (props.TryGetValue("fontSize", out JToken fontSizeToken) && int.TryParse(fontSizeToken.ToString(), out int fontSize))
            {
                Text text = target.GetComponent<Text>();
                if (text != null)
                {
                    Undo.RegisterCompleteObjectUndo(text, "Set UI Font Size");
                    text.fontSize = fontSize;
                    EditorUtility.SetDirty(text);
                }
            }

            if (props.TryGetValue("placeholder", out JToken placeholderToken))
            {
                Text placeholder = target.GetComponentsInChildren<Text>(true).FirstOrDefault(t => t.name.Equals("Placeholder", StringComparison.OrdinalIgnoreCase));
                if (placeholder != null)
                {
                    Undo.RegisterCompleteObjectUndo(placeholder, "Set UI Placeholder Text");
                    placeholder.text = placeholderToken.ToString();
                    EditorUtility.SetDirty(placeholder);
                }
            }
        }

        // --- Utility helpers ---

        private static GameObject FindTarget(JToken token)
        {
            if (token == null)
            {
                return null;
            }

            if (token.Type == JTokenType.Integer && int.TryParse(token.ToString(), out int instanceId))
            {
                return EditorUtility.InstanceIDToObject(instanceId) as GameObject;
            }

            string raw = token.ToString();
            if (string.IsNullOrEmpty(raw))
            {
                return null;
            }

            GameObject byPath = GameObject.Find(raw);
            if (byPath != null)
            {
                return byPath;
            }

            GameObject[] roots = SceneManager.GetActiveScene().GetRootGameObjects();
            foreach (GameObject root in roots)
            {
                Transform match = root.transform.Find(raw);
                if (match != null)
                {
                    return match.gameObject;
                }
            }

            var allObjects = UnityEngine.Object.FindObjectsByType<RectTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            return allObjects.Select(rt => rt.gameObject).FirstOrDefault(go => go.name == raw);
        }

        private static bool ParseVector2(JToken token, out Vector2 value)
        {
            value = default;
            if (token == null)
            {
                return false;
            }

            if (token is JArray array && array.Count >= 2)
            {
                value = new Vector2(array[0].ToObject<float>(), array[1].ToObject<float>());
                return true;
            }

            if (token is JObject obj)
            {
                if (obj.TryGetValue("x", out JToken xToken) && obj.TryGetValue("y", out JToken yToken))
                {
                    value = new Vector2(xToken.ToObject<float>(), yToken.ToObject<float>());
                    return true;
                }
            }

            return false;
        }

        private static bool ParseVector3(JToken token, out Vector3 value)
        {
            value = default;
            if (token == null)
            {
                return false;
            }

            if (token is JArray array && array.Count >= 3)
            {
                value = new Vector3(array[0].ToObject<float>(), array[1].ToObject<float>(), array[2].ToObject<float>());
                return true;
            }

            if (token is JObject obj && obj.TryGetValue("x", out JToken xToken) && obj.TryGetValue("y", out JToken yToken) && obj.TryGetValue("z", out JToken zToken))
            {
                value = new Vector3(xToken.ToObject<float>(), yToken.ToObject<float>(), zToken.ToObject<float>());
                return true;
            }

            return false;
        }

        private static bool ParseFloat(JToken token, out float value)
        {
            value = 0f;
            if (token == null)
            {
                return false;
            }

            return float.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static bool ParseColor(JToken token, out Color color)
        {
            color = Color.white;
            if (token == null)
            {
                return false;
            }

            if (token.Type == JTokenType.String)
            {
                string str = token.ToString();
                if (ColorUtility.TryParseHtmlString(str, out Color parsed))
                {
                    color = parsed;
                    return true;
                }
            }
            else if (token is JObject obj)
            {
                if (obj.TryGetValue("r", out JToken rToken) && obj.TryGetValue("g", out JToken gToken) && obj.TryGetValue("b", out JToken bToken))
                {
                    float r = rToken.ToObject<float>();
                    float g = gToken.ToObject<float>();
                    float b = bToken.ToObject<float>();
                    float a = obj.TryGetValue("a", out JToken aToken) ? aToken.ToObject<float>() : 1f;
                    color = new Color(r, g, b, a);
                    return true;
                }
            }

            return false;
        }

        private static void ClearPersistentListeners(UnityEventBase unityEvent)
        {
            for (int i = unityEvent.GetPersistentEventCount() - 1; i >= 0; i--)
            {
                UnityEventTools.RemovePersistentListener(unityEvent, i);
            }
        }

        private class ActiveAnimation
        {
            public GameObject Target;
            public string Mode;
            public float Duration;
            public double StartTime;
            public Vector3 From;
            public Vector3 To;
            public CanvasGroup CanvasGroup;
            public RectTransform RectTransform;
        }
    }
}
