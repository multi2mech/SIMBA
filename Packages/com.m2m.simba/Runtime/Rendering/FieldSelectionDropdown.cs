using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace M2M.SIMBA
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Dropdown))]
    public sealed class FieldSelectionDropdown : MonoBehaviour
    {
        public FieldColorController controller;
        private Dropdown dropdown;

        private void Awake()
        {
            dropdown = GetComponent<Dropdown>();
            dropdown.onValueChanged.AddListener(OnSelectionChanged);
        }

        private void Start()
        {
            if (controller == null) controller = FindObjectOfType<FieldColorController>();
            if (controller == null) { Debug.LogError("FieldColorController non trovato.", this); return; }
            controller.FieldChanged += HandleControllerFieldChanged;
            RebuildOptions();
        }

        private void OnDestroy()
        {
            dropdown.onValueChanged.RemoveListener(OnSelectionChanged);
            if (controller != null) controller.FieldChanged -= HandleControllerFieldChanged;
        }

        public void RebuildOptions()
        {
            if (controller == null) return;
            string[] names = controller.AvailableFieldNames;
            dropdown.ClearOptions();
            dropdown.AddOptions(new List<string>(names));
            dropdown.SetValueWithoutNotify(Mathf.Clamp(controller.SelectedFieldIndex, 0, Mathf.Max(0, names.Length - 1)));
            dropdown.RefreshShownValue();
        }

        private void OnSelectionChanged(int index) => controller?.SetField(index);
        private void HandleControllerFieldChanged(int index, string name)
        {
            if (dropdown.options.Count == 0) RebuildOptions();
            dropdown.SetValueWithoutNotify(index);
            dropdown.RefreshShownValue();
        }
    }
}
