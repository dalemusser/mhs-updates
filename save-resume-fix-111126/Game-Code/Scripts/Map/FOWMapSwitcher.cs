using Obvious.Soap;
using Sirenix.OdinInspector;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace MHS
{
    public class FOWMapSwitcher : MonoBehaviour
    {
        [SerializeField] private MapDataProcessor _dataProcessor;
        [SerializeField] private BoolVariable _fowIsActive;

        private Image _image;
        private int _currentFOW;
        private List<MapFOWData> _fowData;

        public UnityEvent<string> OnMapChange;

        private void Awake()
        {
            _image = GetComponent<Image>();
            _fowData = _dataProcessor.GetMapData().GetFOWMaps();
        }
        public void ChooseFOWMap(int index)
        {

            if (index >= _fowData.Count) 
            {
                _image.enabled = false;
                _currentFOW = -1;
                _fowIsActive.Value = false; // set, not toggle: a resume replays this call

                Debug.LogWarning("Index out of Range while switching FOW Maps. Turning off FOW.");
                return; 
            }
            _image.enabled = true;
            _fowIsActive.Value = true; // set, not toggle: a resume replays this call
            _image.sprite = _fowData[index].GetFOWSprite();

            string key = index.ToString();

            OnMapChange?.Invoke(key);
        }
        [Button]
        public void NextFOWMap()
        {
            if (_fowData == null) { return; }
            if (_currentFOW >= _fowData.Count)
            {
                ChooseFOWMap(_currentFOW);
                return;
            }
            _currentFOW += 1;
            ChooseFOWMap(_currentFOW);
            
        }
    }
}
