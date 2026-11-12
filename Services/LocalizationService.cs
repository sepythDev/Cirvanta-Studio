using System;
using System.Collections.Generic;

namespace CircuitForge.Services;

public enum AppLanguage
{
    Ru,
    En,
    Es
}

/// <summary>
/// Сервис локализации для CircuitForge (Русский, English, Español).
/// Localization service for CircuitForge (Russian, English, Spanish).
/// Servicio de localización para CircuitForge (Ruso, Inglés, Español).
/// </summary>
public static class LocalizationService
{
    public static AppLanguage CurrentLanguage { get; set; } = AppLanguage.Es;

    public static event Action? LanguageChanged;

    public static void SetLanguage(AppLanguage language)
    {
        if (CurrentLanguage == language) return;
        CurrentLanguage = language;
        LanguageChanged?.Invoke();
    }

    private static readonly Dictionary<string, (string Ru, string En, string Es)> Strings = new()
    {
        // Categories / Категории / Categorías
        ["Cat_Power"] = ("Электропитание", "Power Supply", "Alimentación"),
        ["Cat_Breakers"] = ("Автоматы", "Breakers & RCD", "Interruptores y Diferenciales"),
        ["Cat_Contactors"] = ("Контакторы и переключатели", "Contactors & Switches", "Contactores e Interruptores"),
        ["Cat_Motors"] = ("Моторы", "Motors", "Motores"),
        ["Cat_Buttons"] = ("Кнопки", "Push Buttons", "Pulsadores"),
        ["Cat_Coils"] = ("Катушки и лампы", "Coils & Lamps", "Bobinas y Lámparas"),
        ["Cat_Relays"] = ("Реле", "Relays", "Relés"),
        ["Cat_Other"] = ("Прочее", "Other", "Otros"),

        // Menus / Меню
        ["Menu_File"] = ("Файл", "File", "Archivo"),
        ["Menu_New"] = ("Создать", "New", "Nuevo"),
        ["Menu_Open"] = ("Открыть…", "Open…", "Abrir…"),
        ["Menu_Save"] = ("Сохранить", "Save", "Guardar"),
        ["Menu_Window"] = ("Окно", "Window", "Ventana"),
        ["Menu_Settings"] = ("Настройки", "Settings", "Configuración"),
        ["Menu_Language"] = ("Язык", "Language", "Idioma"),
        ["Menu_Theme"] = ("Тема", "Theme", "Tema"),
        ["Rotate"] = ("↻ Поворот", "↻ Rotate", "↻ Girar"),
        ["Work_Area"] = ("РАБОЧЕЕ ПОЛЕ", "WORKSPACE", "ÁREA DE TRABAJO"),
        ["Theme_Day"] = ("Дневная", "Light", "Claro"),
        ["Theme_Night"] = ("Ночная", "Dark", "Oscuro"),
        ["Back"] = ("‹ Назад", "‹ Back", "‹ Atrás"),

        // Simulation / Симуляция
        ["Run"] = ("Пуск", "Run", "Iniciar"),
        ["Stop"] = ("Стоп", "Stop", "Detener"),
        ["Sim_Stopped"] = ("Симуляция остановлена", "Simulation stopped", "Simulación detenida"),
        ["Sim_Stopped_Edit_Ready"] = ("Симуляция остановлена. Редактирование схемы снова доступно.", "Simulation stopped. Schematic editing is available again.", "Simulación detenida. La edición del esquema está disponible nuevamente."),
        ["Sim_Stop_First"] = ("Останови симуляцию, чтобы редактировать схему.", "Stop the simulation to edit the circuit.", "Detenga la simulación para editar el circuito."),
        ["Sim_Stop_Before_Open"] = ("Останови симуляцию перед открытием другого проекта.", "Stop the simulation before opening another project.", "Detenga la simulación antes de abrir otro proyecto."),
        ["Sim_Stop_Before_Undo"] = ("Останови симуляцию перед отменой действия.", "Stop the simulation before undoing an action.", "Detenga la simulación antes de deshacer una acción."),
        ["Sim_Stop_Before_Rotate"] = ("Останови симуляцию, чтобы поворачивать элементы.", "Stop the simulation to rotate elements.", "Detenga la simulación para girar elementos."),
        ["Sim_No_Active_Source"] = ("Нет включённого источника питания", "No active power source", "No hay fuente de alimentación activa"),
        ["Sim_Short_Circuit_Source"] = ("Короткое замыкание между проводниками источника", "Short circuit between source conductors", "Cortocircuito entre conductores de la fuente"),
        ["Sim_Short_Circuit_Warning"] = ("Короткое замыкание: отключи питание и проверь проводку", "Short circuit: disconnect power and check wiring", "Cortocircuito: desconecte la alimentación y revise el cableado"),
        ["Sim_Active_Loads"] = ("Активных нагрузок: {0}", "Active loads: {0}", "Cargas activas: {0}"),
        ["Sim_Source_On_No_Loads"] = ("Источник включён · нагрузка не активна", "Source ON · no active loads", "Fuente conectada · sin cargas activas"),

        // Wires / Провода
        ["Wire"] = ("Провод", "Wire", "Cable"),
        ["Wire_Color"] = ("Цвет провода", "Wire Color", "Color del cable"),
        ["Wire_Color_Brown"] = ("Коричневый", "Brown", "Marrón"),
        ["Wire_Color_Black"] = ("Чёрный", "Black", "Negro"),
        ["Wire_Color_Grey"] = ("Серый", "Grey", "Gris"),
        ["Wire_Color_Blue"] = ("Синий", "Blue", "Azul"),
        ["Wire_Color_GreenYellow"] = ("Жёлто-зелёный", "Green-Yellow", "Verde-Amarillo"),
        ["Wire_Color_Red"] = ("Красный", "Red", "Rojo"),

        // Lamp glow colors / Цвета свечения ламп
        ["Glow_Color"] = ("Цвет свечения", "Glow Color", "Color de brillo"),
        ["Glow_Yellow"] = ("Жёлтый", "Yellow", "Amarillo"),
        ["Glow_Orange"] = ("Оранжевый", "Orange", "Naranja"),
        ["Glow_Red"] = ("Красный", "Red", "Rojo"),
        ["Glow_Green"] = ("Зелёный", "Green", "Verde"),

        // Components / Компоненты
        ["Comp_PowerPhase"] = ("Фаза", "Phase", "Fase"),
        ["Comp_PowerNeutral"] = ("Нейтраль", "Neutral", "Neutro"),
        ["Comp_PowerEarth"] = ("Земля", "Earth PE", "Tierra PE"),
        ["Comp_PowerPhaseNeutral"] = ("Фаза и нейтраль", "Phase & Neutral", "Fase y Neutro"),
        ["Comp_PowerPhaseNeutralEarth"] = ("Фаза, нейтраль и земля", "Phase, Neutral & Earth", "Fase, Neutro y Tierra"),
        ["Comp_PowerThreePhase"] = ("Три фазы", "Three Phase", "Trifásico"),
        ["Comp_PowerThreePhaseEarth"] = ("Три фазы и земля", "Three Phase & Earth", "Trifásico y Tierra"),
        ["Comp_PowerThreePhaseNeutral"] = ("Три фазы и нейтраль", "Three Phase & Neutral", "Trifásico y Neutro"),
        ["Comp_PowerThreePhaseNeutralEarth"] = ("Три фазы, нейтраль и земля", "Three Phase, Neutral & Earth", "Trifásico, Neutro y Tierra"),
        ["Comp_PowerPlus"] = ("Плюс DC", "DC Plus (+)", "Positivo DC (+)"),
        ["Comp_PowerMinus"] = ("Минус DC", "DC Minus (−)", "Negativo DC (−)"),
        ["Comp_PowerDcEarth"] = ("Земля DC", "DC Earth", "Tierra DC"),
        ["Comp_AcSupply"] = ("Источник AC", "AC Supply", "Fuente AC"),
        ["Comp_Supply24V"] = ("24V DC", "24V DC", "24V DC"),

        ["Comp_Breaker1P"] = ("Автомат 1P", "1P Breaker", "Disyuntor 1P"),
        ["Comp_Breaker1PN"] = ("Автомат 1P+N", "1P+N Breaker", "Disyuntor 1P+N"),
        ["Comp_Breaker2P"] = ("Автомат 2P", "2P Breaker", "Disyuntor 2P"),
        ["Comp_Breaker3P"] = ("Автомат 3P", "3P Breaker", "Disyuntor 3P"),
        ["Comp_Breaker3PN"] = ("Автомат 3P+N", "3P+N Breaker", "Disyuntor 3P+N"),
        ["Comp_Rcd1PN"] = ("УЗО 1P+N", "RCD 1P+N", "Diferencial 1P+N"),
        ["Comp_Rcd3PN"] = ("УЗО 3P+N", "RCD 3P+N", "Diferencial 3P+N"),
        ["Comp_ThermalRelay"] = ("Тепловое реле", "Thermal Overload Relay", "Relé Térmico"),
        ["Comp_OverloadNC"] = ("Тепловая защита", "Thermal Cutout NC", "Protección Térmica NC"),
        ["Comp_OverloadNO"] = ("Термореле NO (97-98)", "Thermal Relay NO (97-98)", "Relé Térmico NA (97-98)"),
        ["Comp_ThermalRelayAuxNC"] = ("Термореле NC (95-96)", "Thermal Relay NC (95-96)", "Relé Térmico NC (95-96)"),
        ["Comp_ThermalRelayAuxNO_NC"] = ("Термореле NC+NO (95-96/97-98)", "Thermal Relay NC+NO", "Relé Térmico NC+NA"),

        ["Comp_Contactor2P"] = ("Контактор 2-полюсный", "2-Pole Contactor", "Contactor Bipolar 2P"),
        ["Comp_Contactor3P"] = ("Контактор 3-полюсный", "3-Pole Contactor", "Contactor Tripolar 3P"),
        ["Comp_Contactor4P"] = ("Контактор 4-полюсный", "4-Pole Contactor", "Contactor Tetrapolar 4P"),
        ["Comp_AuxContactNO"] = ("Контакт NO", "Aux Contact NO", "Contacto Auxiliar NA"),
        ["Comp_AuxContactNC"] = ("Контакт NC", "Aux Contact NC", "Contacto Auxiliar NC"),
        ["Comp_AuxContactNO_NC"] = ("Контакт NO+NC", "Aux Contact NO+NC", "Contacto Auxiliar NA+NC"),
        ["Comp_ConContact"] = ("CON contact", "Changeover Contact", "Contacto Conmutado"),

        ["Comp_PushButtonNO"] = ("Кнопка NO", "Push Button NO", "Pulsador NA"),
        ["Comp_PushButtonNC"] = ("Кнопка NC", "Push Button NC", "Pulsador NC"),
        ["Comp_PushButtonNO_NC"] = ("Кнопка NO+NC", "Push Button NO+NC", "Pulsador NA+NC"),
        ["Comp_PushButtonCon"] = ("Кнопка CON", "Push Button CON", "Pulsador Conmutador CON"),
        ["Comp_ToggleSwitchNO"] = ("Переключатель NO", "Toggle Switch NO", "Interruptor NA"),
        ["Comp_ToggleSwitchNC"] = ("Переключатель NC", "Toggle Switch NC", "Interruptor NC"),
        ["Comp_ToggleSwitchNO_NC"] = ("Переключатель NO+NC", "Toggle Switch NO+NC", "Interruptor NA+NC"),
        ["Comp_ToggleSwitchCon"] = ("Переключатель CON", "Toggle Switch CON", "Interruptor Conmutador CON"),
        ["Comp_CrossoverSwitch"] = ("Crossover switch", "Crossover Switch", "Interruptor Cruzamiento"),
        ["Comp_LimitSwitchNO"] = ("Концевик NO", "Limit Switch NO", "Final de Carrera NA"),
        ["Comp_LimitSwitchNC"] = ("Концевик NC", "Limit Switch NC", "Final de Carrera NC"),

        ["Comp_Coil"] = ("Катушка", "Coil", "Bobina"),
        ["Comp_Lamp"] = ("Лампа", "Lamp", "Lámpara"),
        ["Comp_BlinkingLamp"] = ("Лампа мигающая", "Blinking Lamp", "Lámpara Intermitente"),
        ["Comp_AcMotor"] = ("Двигатель AC 3~", "3-Phase AC Motor", "Motor Trifásico AC"),
        ["Comp_AcMotor1P"] = ("Двигатель AC 1~", "1-Phase AC Motor", "Motor Monofásico AC"),
        ["Comp_DcMotor"] = ("Двигатель DC", "DC Motor", "Motor DC"),

        // Dialogs & Actions
        ["ImportedFromCadSimu"] = ("Импортировано из CADe SIMU", "Imported from CADe SIMU", "Importado desde CADe SIMU"),
        ["Opened"] = ("Открыто", "Opened", "Abierto"),
        ["Edit_Tag_Title"] = ("Изменить обозначение", "Edit Tag", "Editar Referencia"),
        ["Edit_Tag_Prompt"] = ("Введите позиционное обозначение:", "Enter reference designation:", "Introduzca la referencia del componente:"),
        ["Save_Action"] = ("Сохранить", "Save", "Guardar"),
        ["Cancel_Action"] = ("Отмена", "Cancel", "Cancelar"),
        ["Ready_Status"] = ("Готово. Добавь источник, компоненты и соедини их терминалы проводами.", "Ready. Add source, components and wire their terminals.", "Listo. Agregue fuentes, componentes y conecte sus bornes."),
        ["Zoom_Status"] = ("Масштаб схемы:", "Schematic zoom:", "Escala del esquema:"),
        ["Wire_Mode_Status"] = ("Провод · цвет: {0}. Выбери две клеммы.", "Wire · color: {0}. Select two terminals.", "Cable · color: {0}. Seleccione dos bornes."),
        ["Wire_Connected"] = ("Провод добавлен.", "Wire connected.", "Cable conectado."),
        ["Undo_Done"] = ("Последнее действие отменено (Ctrl+Z).", "Last action undone (Ctrl+Z).", "Última acción deshecha (Ctrl+Z)."),
        ["Nothing_To_Undo"] = ("Отменять больше нечего.", "Nothing to undo.", "No hay nada que deshacer.")
    };

    public static string Get(string key, params object[] args)
    {
        if (!Strings.TryGetValue(key, out var tuple)) return key;

        var format = CurrentLanguage switch
        {
            AppLanguage.En => tuple.En,
            AppLanguage.Es => tuple.Es,
            _ => tuple.Ru
        };

        return args.Length > 0 ? string.Format(format, args) : format;
    }
}
