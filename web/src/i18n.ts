import { toGregorian, zoomUnit, type Tick } from './timeAxis';

/**
 * Локализация интерфейса и подписей дат.
 *
 * Даты не хранятся строками в базе, а собираются здесь через Intl.
 * Причина простая: строк пришлось бы держать по одной на язык, и добавление
 * одиннадцатого языка означало бы переимпорт всех данных. Intl знает названия
 * месяцев и обозначения эр для всех локалей, а на оси лежит достаточно —
 * интервал и точность.
 *
 * Десятилетия, века и тысячелетия Intl не умеет, поэтому они собираются
 * функциями: подпись слишком по-разному устроена в разных языках, чтобы
 * обойтись шаблоном со вставкой. Русский ставит римскую цифру перед словом,
 * немецкий — арабскую с точкой, китайский и японский пишут эру префиксом.
 */

export const LANGUAGES = [
  { code: 'en', name: 'English' },
  { code: 'zh', name: '中文' },
  { code: 'hi', name: 'हिन्दी' },
  { code: 'es', name: 'Español' },
  { code: 'ar', name: 'العربية' },
  { code: 'fr', name: 'Français' },
  { code: 'pt', name: 'Português' },
  { code: 'ru', name: 'Русский' },
  { code: 'de', name: 'Deutsch' },
  { code: 'ja', name: '日本語' },
] as const;

export type LangCode = (typeof LANGUAGES)[number]['code'];

export const RTL_LANGUAGES: readonly string[] = ['ar'];

const ROMAN: [number, string][] = [
  [1000, 'M'], [900, 'CM'], [500, 'D'], [400, 'CD'],
  [100, 'C'], [90, 'XC'], [50, 'L'], [40, 'XL'],
  [10, 'X'], [9, 'IX'], [5, 'V'], [4, 'IV'], [1, 'I'],
];

export function roman(n: number): string {
  if (n <= 0) return String(n);
  let out = '';
  let rest = n;
  for (const [value, sign] of ROMAN) {
    while (rest >= value) {
      out += sign;
      rest -= value;
    }
  }
  return out;
}

/**
 * Согласование числительных.
 *
 * Форм в языках разное количество и правила у всех свои: английскому хватает
 * двух, русскому нужно три («1 событие», «2 события», «5 событий»), арабскому
 * шесть, китайскому и японскому ни одной. Правила знает Intl.PluralRules,
 * а словари дают только сами формы — так одиннадцатый язык не потребует
 * переписывать логику.
 */
const pluralCache = new Map<string, Intl.PluralRules>();

export function plural(n: number, forms: Partial<Record<Intl.LDMLPluralRule, string>>): string {
  let rules = pluralCache.get(current);
  if (!rules) pluralCache.set(current, (rules = new Intl.PluralRules(current)));

  const form = forms[rules.select(n)] ?? forms.other ?? '';
  return form.replace('#', String(n));
}

function ordinalEn(n: number): string {
  const lastTwo = n % 100;
  if (lastTwo >= 11 && lastTwo <= 13) return `${n}th`;
  return `${n}${(['th', 'st', 'nd', 'rd'][n % 10] ?? 'th')}`;
}

export interface Dict {
  appTitle: string;
  searchPlaceholder: string;
  nothingFound: string;

  zoomIn: string;
  zoomOut: string;
  goToday: string;
  wholeHistory: string;

  theme: string;
  themeAuto: string;
  themeLight: string;
  themeDark: string;
  language: string;

  fields: string;
  kindsHeading: string;
  layout: string;
  layoutHint: string;
  colorByKind: string;
  help: string;
  helpWheel: string;
  helpDrag: string;
  helpCluster: string;
  helpHover: string;

  above: string;
  below: string;

  loading: string;
  apiUnavailable: string;
  eventsInView: (n: number) => string;
  clusterTitle: (n: number) => string;
  clusterHint: string;
  /** Подсказка для кластера, который приближением уже не разложить. */
  clusterHintList: string;
  listShowing: (shown: number, total: number) => string;
  listMore: string;
  close: string;

  bandLabel: string;
  bandEmpty: string;
  bandMore: (shown: number, hidden: number) => string;

  support: string;
  supportHint: string;
  sourceCode: string;
  dataPrefix: string;
  /** «сборка 2026-07-30» — когда данные последний раз пересобирались. */
  built: (date: string) => string;
  disclaimer: string;
  reportIssue: string;

  kinds: Record<string, string>;
  categories: Record<string, string>;

  /** «1920-е годы» — принимает первый год десятилетия по историческому счёту. */
  decade: (year: number, bc: boolean) => string;
  century: (index: number, bc: boolean) => string;
  millennium: (index: number, bc: boolean) => string;
  circa: (label: string) => string;
}

const en: Dict = {
  appTitle: 'History of science timeline',
  searchPlaceholder: 'Find a discovery, invention, scientist…',
  nothingFound: 'nothing found',
  zoomIn: 'Zoom in',
  zoomOut: 'Zoom out',
  goToday: 'Today',
  wholeHistory: 'Whole history',
  theme: 'Theme',
  themeAuto: 'System',
  themeLight: 'Light',
  themeDark: 'Dark',
  language: 'Language',
  fields: 'Fields of science',
  kindsHeading: 'Event types',
  layout: 'Layout',
  layoutHint: 'Above the line — new knowledge, below — change and application. Any type can be moved to the other side.',
  colorByKind: 'Colour by event type',
  help: 'How to use',
  helpWheel: 'Mouse wheel — smooth zoom from millennia to days',
  helpDrag: 'Drag — move through time',
  helpCluster: 'Circle with a number — a cluster: click to zoom in, or to see the list when there is nothing left to zoom into',
  helpHover: 'Hover — event card',
  above: '↑ above',
  below: '↓ below',
  loading: 'loading…',
  apiUnavailable: 'API unavailable',
  eventsInView: (n) => plural(n, { one: '# event in view', other: '# events in view' }),
  clusterTitle: (n) => plural(n, { one: '# event', other: '# events' }),
  clusterHint: 'click to zoom into this interval',
  clusterHintList: 'click to see the list',
  listShowing: (shown, total) => `showing ${shown} of ${total}`,
  listMore: 'show more',
  close: 'close',
  bandLabel: 'dated less precisely than the current scale',
  bandEmpty: 'no such events here',
  bandMore: (shown, hidden) => `showing ${shown}, ${hidden} more`,
  support: 'Support',
  supportHint: 'If this is useful, you can chip in.',
  sourceCode: 'Source code',
  dataPrefix: 'Data:',
  built: (date) => `built ${date}`,
  disclaimer: 'Data is gathered automatically and may be incomplete or out of date — check the primary sources.',
  reportIssue: 'Report a problem',
  kinds: {
    discovery: 'Discoveries',
    observation: 'Observations',
    confirmation: 'Confirmations',
    publication: 'Publications',
    invention: 'Inventions',
    refutation: 'Refutations',
    award: 'Awards',
    other: 'Other',
  },
  categories: {
    physics: 'Physics',
    chemistry: 'Chemistry',
    biology: 'Biology',
    medicine: 'Medicine',
    astronomy: 'Astronomy',
    mathematics: 'Mathematics',
    computing: 'Computer science',
    earth: 'Earth science',
    engineering: 'Engineering',
    psychology: 'Psychology',
    social: 'Social science',
  },
  decade: (year, bc) => (bc ? `${year}s BC` : `${year}s`),
  century: (i, bc) => (bc ? `${ordinalEn(i)} century BC` : `${ordinalEn(i)} century`),
  millennium: (i, bc) => (bc ? `${ordinalEn(i)} millennium BC` : `${ordinalEn(i)} millennium`),
  circa: (label) => `c. ${label}`,
};

const ru: Dict = {
  appTitle: 'Лента истории науки',
  searchPlaceholder: 'Найти открытие, изобретение, учёного…',
  nothingFound: 'ничего не нашлось',
  zoomIn: 'Приблизить',
  zoomOut: 'Отдалить',
  goToday: 'Сегодня',
  wholeHistory: 'Вся история',
  theme: 'Тема',
  themeAuto: 'Как в системе',
  themeLight: 'Светлая',
  themeDark: 'Тёмная',
  language: 'Язык',
  fields: 'Области науки',
  kindsHeading: 'Типы событий',
  layout: 'Раскладка',
  layoutHint: 'Над линией — новое знание, под линией — изменения и применение. Любой тип можно перекинуть на другую сторону.',
  colorByKind: 'Цвет по типу события',
  help: 'Как пользоваться',
  helpWheel: 'Колесо мыши — плавный зум от тысячелетий до суток',
  helpDrag: 'Перетаскивание — движение по времени',
  helpCluster: 'Круг с числом — кластер: клик приближает, а если приближать уже нечего — показывает список',
  helpHover: 'Наведение — карточка события',
  above: '↑ сверху',
  below: '↓ снизу',
  loading: 'загрузка…',
  apiUnavailable: 'API недоступен',
  eventsInView: (n) => plural(n, {
    one: '# событие в поле зрения',
    few: '# события в поле зрения',
    many: '# событий в поле зрения',
  }),
  clusterTitle: (n) => plural(n, { one: '# событие', few: '# события', many: '# событий' }),
  clusterHint: 'клик приближает этот интервал',
  clusterHintList: 'клик покажет список',
  listShowing: (shown, total) => `показано ${shown} из ${total}`,
  listMore: 'показать ещё',
  close: 'закрыть',
  bandLabel: 'дата известна неточнее текущего масштаба',
  bandEmpty: 'таких событий здесь нет',
  bandMore: (shown, hidden) => `показано ${shown}, ещё ${hidden}`,
  support: 'Поддержать',
  supportHint: 'Если лента пригодилась — можно поддержать.',
  sourceCode: 'Исходный код',
  dataPrefix: 'Данные:',
  built: (date) => `сборка ${date}`,
  disclaimer: 'Данные собраны автоматически и могут содержать неточности — сверяйтесь с первоисточниками.',
  reportIssue: 'Сообщить об ошибке',
  kinds: {
    discovery: 'Открытия',
    observation: 'Наблюдения',
    confirmation: 'Подтверждения',
    publication: 'Публикации',
    invention: 'Изобретения',
    refutation: 'Опровержения',
    award: 'Премии',
    other: 'Прочее',
  },
  categories: {
    physics: 'Физика',
    chemistry: 'Химия',
    biology: 'Биология',
    medicine: 'Медицина',
    astronomy: 'Астрономия',
    mathematics: 'Математика',
    computing: 'Информатика',
    earth: 'Науки о Земле',
    engineering: 'Техника',
    psychology: 'Психология',
    social: 'Общественные науки',
  },
  decade: (year, bc) => (bc ? `${year}-е годы до н. э.` : `${year}-е годы`),
  century: (i, bc) => (bc ? `${roman(i)} век до н. э.` : `${roman(i)} век`),
  millennium: (i, bc) => (bc ? `${roman(i)} тысячелетие до н. э.` : `${roman(i)} тысячелетие`),
  circa: (label) => `около ${label}`,
};

const es: Dict = {
  ...en,
  appTitle: 'Línea del tiempo de la historia de la ciencia',
  searchPlaceholder: 'Buscar un descubrimiento, invento, científico…',
  nothingFound: 'no se encontró nada',
  zoomIn: 'Acercar',
  zoomOut: 'Alejar',
  goToday: 'Hoy',
  wholeHistory: 'Toda la historia',
  theme: 'Tema',
  themeAuto: 'Del sistema',
  themeLight: 'Claro',
  themeDark: 'Oscuro',
  language: 'Idioma',
  fields: 'Áreas de la ciencia',
  kindsHeading: 'Tipos de evento',
  layout: 'Disposición',
  layoutHint: 'Sobre la línea, el conocimiento nuevo; debajo, los cambios y las aplicaciones. Cualquier tipo puede cambiarse de lado.',
  colorByKind: 'Color por tipo de evento',
  help: 'Cómo usarlo',
  helpWheel: 'Rueda del ratón: zoom continuo de milenios a días',
  helpDrag: 'Arrastrar: desplazarse en el tiempo',
  helpCluster: 'Círculo con número: un grupo. Haz clic para acercar, o para ver la lista si ya no hay nada que acercar',
  helpHover: 'Pasar el cursor: ficha del evento',
  above: '↑ arriba',
  below: '↓ abajo',
  loading: 'cargando…',
  apiUnavailable: 'API no disponible',
  eventsInView: (n) => plural(n, { one: '# evento a la vista', other: '# eventos a la vista' }),
  clusterTitle: (n) => plural(n, { one: '# evento', other: '# eventos' }),
  clusterHint: 'haz clic para acercar este intervalo',
  clusterHintList: 'haz clic para ver la lista',
  listShowing: (shown, total) => `mostrando ${shown} de ${total}`,
  listMore: 'mostrar más',
  close: 'cerrar',
  bandLabel: 'fecha menos precisa que la escala actual',
  bandEmpty: 'aquí no hay eventos así',
  bandMore: (shown, hidden) => `se muestran ${shown}, ${hidden} más`,
  support: 'Apoyar',
  supportHint: 'Si te resulta útil, puedes contribuir.',
  sourceCode: 'Código fuente',
  dataPrefix: 'Datos:',
  built: (date) => `compilación ${date}`,
  disclaimer: 'Los datos se recopilan automáticamente y pueden ser incompletos — consulta las fuentes originales.',
  reportIssue: 'Informar de un problema',
  kinds: {
    discovery: 'Descubrimientos',
    observation: 'Observaciones',
    confirmation: 'Confirmaciones',
    publication: 'Publicaciones',
    invention: 'Inventos',
    refutation: 'Refutaciones',
    award: 'Premios',
    other: 'Otros',
  },
  categories: {
    physics: 'Física',
    chemistry: 'Química',
    biology: 'Biología',
    medicine: 'Medicina',
    astronomy: 'Astronomía',
    mathematics: 'Matemáticas',
    computing: 'Informática',
    earth: 'Ciencias de la Tierra',
    engineering: 'Ingeniería',
    psychology: 'Psicología',
    social: 'Ciencias sociales',
  },
  decade: (year, bc) => (bc ? `años ${year} a. C.` : `años ${year}`),
  century: (i, bc) => (bc ? `siglo ${roman(i)} a. C.` : `siglo ${roman(i)}`),
  millennium: (i, bc) => (bc ? `${roman(i)} milenio a. C.` : `${roman(i)} milenio`),
  circa: (label) => `h. ${label}`,
};

const fr: Dict = {
  ...en,
  appTitle: "Frise de l'histoire des sciences",
  searchPlaceholder: 'Chercher une découverte, une invention, un scientifique…',
  nothingFound: 'aucun résultat',
  zoomIn: 'Zoom avant',
  zoomOut: 'Zoom arrière',
  goToday: "Aujourd'hui",
  wholeHistory: 'Toute la période',
  theme: 'Thème',
  themeAuto: 'Système',
  themeLight: 'Clair',
  themeDark: 'Sombre',
  language: 'Langue',
  fields: 'Domaines scientifiques',
  kindsHeading: "Types d'événement",
  layout: 'Disposition',
  layoutHint: "Au-dessus de la ligne, les connaissances nouvelles ; en dessous, les changements et les applications. Chaque type peut changer de côté.",
  colorByKind: "Couleur par type d'événement",
  help: 'Mode d’emploi',
  helpWheel: 'Molette : zoom continu, des millénaires aux jours',
  helpDrag: 'Glisser : se déplacer dans le temps',
  helpCluster: 'Cercle avec un nombre : un groupe. Cliquez pour zoomer, ou pour voir la liste quand il n’y a plus rien à zoomer',
  helpHover: 'Survol : fiche de l’événement',
  above: '↑ au-dessus',
  below: '↓ en dessous',
  loading: 'chargement…',
  apiUnavailable: 'API indisponible',
  eventsInView: (n) => plural(n, { one: '# événement visible', other: '# événements visibles' }),
  clusterTitle: (n) => plural(n, { one: '# événement', other: '# événements' }),
  clusterHint: 'cliquez pour zoomer sur cet intervalle',
  clusterHintList: 'cliquez pour voir la liste',
  listShowing: (shown, total) => `${shown} sur ${total} affichés`,
  listMore: 'afficher plus',
  close: 'fermer',
  bandLabel: 'date moins précise que l’échelle actuelle',
  bandEmpty: 'aucun événement de ce type ici',
  bandMore: (shown, hidden) => `${shown} affichés, ${hidden} de plus`,
  support: 'Soutenir',
  supportHint: 'Si cela vous est utile, vous pouvez contribuer.',
  sourceCode: 'Code source',
  dataPrefix: 'Données :',
  built: (date) => `version ${date}`,
  disclaimer: 'Les données sont collectées automatiquement et peuvent être incomplètes — vérifiez les sources primaires.',
  reportIssue: 'Signaler un problème',
  kinds: {
    discovery: 'Découvertes',
    observation: 'Observations',
    confirmation: 'Confirmations',
    publication: 'Publications',
    invention: 'Inventions',
    refutation: 'Réfutations',
    award: 'Prix',
    other: 'Autres',
  },
  categories: {
    physics: 'Physique',
    chemistry: 'Chimie',
    biology: 'Biologie',
    medicine: 'Médecine',
    astronomy: 'Astronomie',
    mathematics: 'Mathématiques',
    computing: 'Informatique',
    earth: 'Sciences de la Terre',
    engineering: 'Ingénierie',
    psychology: 'Psychologie',
    social: 'Sciences sociales',
  },
  decade: (year, bc) => (bc ? `années ${year} av. J.-C.` : `années ${year}`),
  century: (i, bc) => (bc ? `${roman(i)}ᵉ siècle av. J.-C.` : `${roman(i)}ᵉ siècle`),
  millennium: (i, bc) => (bc ? `${roman(i)}ᵉ millénaire av. J.-C.` : `${roman(i)}ᵉ millénaire`),
  circa: (label) => `v. ${label}`,
};

const de: Dict = {
  ...en,
  appTitle: 'Zeitleiste der Wissenschaftsgeschichte',
  searchPlaceholder: 'Entdeckung, Erfindung oder Person suchen…',
  nothingFound: 'nichts gefunden',
  zoomIn: 'Vergrößern',
  zoomOut: 'Verkleinern',
  goToday: 'Heute',
  wholeHistory: 'Gesamte Geschichte',
  theme: 'Design',
  themeAuto: 'Wie im System',
  themeLight: 'Hell',
  themeDark: 'Dunkel',
  language: 'Sprache',
  fields: 'Wissenschaftsgebiete',
  kindsHeading: 'Ereignistypen',
  layout: 'Anordnung',
  layoutHint: 'Über der Linie neues Wissen, darunter Veränderung und Anwendung. Jeder Typ lässt sich auf die andere Seite legen.',
  colorByKind: 'Farbe nach Ereignistyp',
  help: 'Bedienung',
  helpWheel: 'Mausrad — stufenloser Zoom von Jahrtausenden bis zu Tagen',
  helpDrag: 'Ziehen — durch die Zeit bewegen',
  helpCluster: 'Kreis mit Zahl — eine Gruppe: Klick zoomt hinein, oder zeigt die Liste, wenn es nichts mehr zu zoomen gibt',
  helpHover: 'Zeigen — Ereigniskarte',
  above: '↑ oben',
  below: '↓ unten',
  loading: 'lädt…',
  apiUnavailable: 'API nicht erreichbar',
  eventsInView: (n) => plural(n, { one: '# Ereignis im Blick', other: '# Ereignisse im Blick' }),
  clusterTitle: (n) => plural(n, { one: '# Ereignis', other: '# Ereignisse' }),
  clusterHint: 'Klick zoomt in diesen Zeitraum',
  clusterHintList: 'Klick zeigt die Liste',
  listShowing: (shown, total) => `${shown} von ${total} angezeigt`,
  listMore: 'mehr anzeigen',
  close: 'schließen',
  bandLabel: 'ungenauer datiert als der aktuelle Maßstab',
  bandEmpty: 'hier gibt es keine solchen Ereignisse',
  bandMore: (shown, hidden) => `${shown} gezeigt, ${hidden} weitere`,
  support: 'Unterstützen',
  supportHint: 'Wenn es nützlich ist, kannst du etwas beitragen.',
  sourceCode: 'Quellcode',
  dataPrefix: 'Daten:',
  built: (date) => `Stand ${date}`,
  disclaimer: 'Die Daten werden automatisch gesammelt und können unvollständig sein — prüfe die Originalquellen.',
  reportIssue: 'Problem melden',
  kinds: {
    discovery: 'Entdeckungen',
    observation: 'Beobachtungen',
    confirmation: 'Bestätigungen',
    publication: 'Veröffentlichungen',
    invention: 'Erfindungen',
    refutation: 'Widerlegungen',
    award: 'Preise',
    other: 'Sonstiges',
  },
  categories: {
    physics: 'Physik',
    chemistry: 'Chemie',
    biology: 'Biologie',
    medicine: 'Medizin',
    astronomy: 'Astronomie',
    mathematics: 'Mathematik',
    computing: 'Informatik',
    earth: 'Geowissenschaften',
    engineering: 'Technik',
    psychology: 'Psychologie',
    social: 'Sozialwissenschaften',
  },
  decade: (year, bc) => (bc ? `${year}er Jahre v. Chr.` : `${year}er Jahre`),
  century: (i, bc) => (bc ? `${i}. Jahrhundert v. Chr.` : `${i}. Jahrhundert`),
  millennium: (i, bc) => (bc ? `${i}. Jahrtausend v. Chr.` : `${i}. Jahrtausend`),
  circa: (label) => `um ${label}`,
};

const pt: Dict = {
  ...en,
  appTitle: 'Linha do tempo da história da ciência',
  searchPlaceholder: 'Procurar uma descoberta, invenção, cientista…',
  nothingFound: 'nada encontrado',
  zoomIn: 'Aproximar',
  zoomOut: 'Afastar',
  goToday: 'Hoje',
  wholeHistory: 'Toda a história',
  theme: 'Tema',
  themeAuto: 'Do sistema',
  themeLight: 'Claro',
  themeDark: 'Escuro',
  language: 'Idioma',
  fields: 'Áreas da ciência',
  kindsHeading: 'Tipos de evento',
  layout: 'Disposição',
  layoutHint: 'Acima da linha, o conhecimento novo; abaixo, as mudanças e aplicações. Qualquer tipo pode mudar de lado.',
  colorByKind: 'Cor por tipo de evento',
  help: 'Como usar',
  helpWheel: 'Roda do rato — zoom contínuo de milénios a dias',
  helpDrag: 'Arrastar — mover no tempo',
  helpCluster: 'Círculo com número — um grupo: clique para aproximar, ou para ver a lista quando já não há o que aproximar',
  helpHover: 'Passar o cursor — ficha do evento',
  above: '↑ acima',
  below: '↓ abaixo',
  loading: 'a carregar…',
  apiUnavailable: 'API indisponível',
  eventsInView: (n) => plural(n, { one: '# evento à vista', other: '# eventos à vista' }),
  clusterTitle: (n) => plural(n, { one: '# evento', other: '# eventos' }),
  clusterHint: 'clique para aproximar este intervalo',
  clusterHintList: 'clique para ver a lista',
  listShowing: (shown, total) => `a mostrar ${shown} de ${total}`,
  listMore: 'mostrar mais',
  close: 'fechar',
  bandLabel: 'data menos precisa do que a escala atual',
  bandEmpty: 'não há eventos assim aqui',
  bandMore: (shown, hidden) => `a mostrar ${shown}, mais ${hidden}`,
  support: 'Apoiar',
  supportHint: 'Se for útil, podes contribuir.',
  sourceCode: 'Código-fonte',
  dataPrefix: 'Dados:',
  built: (date) => `compilação ${date}`,
  disclaimer: 'Os dados são recolhidos automaticamente e podem estar incompletos — consulta as fontes originais.',
  reportIssue: 'Comunicar um problema',
  kinds: {
    discovery: 'Descobertas',
    observation: 'Observações',
    confirmation: 'Confirmações',
    publication: 'Publicações',
    invention: 'Invenções',
    refutation: 'Refutações',
    award: 'Prémios',
    other: 'Outros',
  },
  categories: {
    physics: 'Física',
    chemistry: 'Química',
    biology: 'Biologia',
    medicine: 'Medicina',
    astronomy: 'Astronomia',
    mathematics: 'Matemática',
    computing: 'Informática',
    earth: 'Ciências da Terra',
    engineering: 'Engenharia',
    psychology: 'Psicologia',
    social: 'Ciências sociais',
  },
  decade: (year, bc) => (bc ? `anos ${year} a.C.` : `anos ${year}`),
  century: (i, bc) => (bc ? `século ${roman(i)} a.C.` : `século ${roman(i)}`),
  millennium: (i, bc) => (bc ? `${roman(i)}.º milénio a.C.` : `${roman(i)}.º milénio`),
  circa: (label) => `c. ${label}`,
};

const zh: Dict = {
  ...en,
  appTitle: '科学史时间轴',
  searchPlaceholder: '搜索发现、发明或科学家…',
  nothingFound: '没有找到',
  zoomIn: '放大',
  zoomOut: '缩小',
  goToday: '今天',
  wholeHistory: '全部历史',
  theme: '主题',
  themeAuto: '跟随系统',
  themeLight: '浅色',
  themeDark: '深色',
  language: '语言',
  fields: '科学领域',
  kindsHeading: '事件类型',
  layout: '布局',
  layoutHint: '线上方是新知识，下方是变化与应用。任何类型都可以换到另一侧。',
  colorByKind: '按事件类型着色',
  help: '使用方法',
  helpWheel: '鼠标滚轮 — 从千年到日的平滑缩放',
  helpDrag: '拖动 — 在时间中移动',
  helpCluster: '带数字的圆 — 聚合：点击可放大；若已无法再放大，则显示列表',
  helpHover: '悬停 — 事件卡片',
  above: '↑ 上方',
  below: '↓ 下方',
  loading: '加载中…',
  apiUnavailable: 'API 不可用',
  eventsInView: (n) => plural(n, { other: '视野内有 # 个事件' }),
  clusterTitle: (n) => plural(n, { other: '# 个事件' }),
  clusterHint: '点击放大此区间',
  clusterHintList: '点击查看列表',
  listShowing: (shown, total) => `显示 ${shown} / ${total}`,
  listMore: '显示更多',
  close: '关闭',
  bandLabel: '日期精度低于当前比例',
  bandEmpty: '这里没有这类事件',
  bandMore: (shown, hidden) => `已显示 ${shown} 个，还有 ${hidden} 个`,
  support: '支持',
  supportHint: '如果觉得有用，可以支持一下。',
  sourceCode: '源代码',
  dataPrefix: '数据：',
  built: (date) => `构建于 ${date}`,
  disclaimer: '数据为自动采集，可能不完整或已过时，请核对原始来源。',
  reportIssue: '报告问题',
  kinds: {
    discovery: '发现',
    observation: '观测',
    confirmation: '证实',
    publication: '论文',
    invention: '发明',
    refutation: '反驳',
    award: '奖项',
    other: '其他',
  },
  categories: {
    physics: '物理学',
    chemistry: '化学',
    biology: '生物学',
    medicine: '医学',
    astronomy: '天文学',
    mathematics: '数学',
    computing: '计算机科学',
    earth: '地球科学',
    engineering: '工程学',
    psychology: '心理学',
    social: '社会科学',
  },
  decade: (year, bc) => (bc ? `公元前 ${year} 年代` : `${year} 年代`),
  century: (i, bc) => (bc ? `公元前 ${i} 世纪` : `${i} 世纪`),
  millennium: (i, bc) => (bc ? `公元前 ${i} 千年` : `${i} 千年`),
  circa: (label) => `约 ${label}`,
};

const ja: Dict = {
  ...en,
  appTitle: '科学史タイムライン',
  searchPlaceholder: '発見・発明・科学者を検索…',
  nothingFound: '見つかりませんでした',
  zoomIn: '拡大',
  zoomOut: '縮小',
  goToday: '今日',
  wholeHistory: '全期間',
  theme: 'テーマ',
  themeAuto: 'システムに合わせる',
  themeLight: 'ライト',
  themeDark: 'ダーク',
  language: '言語',
  fields: '科学分野',
  kindsHeading: '出来事の種類',
  layout: '配置',
  layoutHint: '線の上は新しい知識、下は変化と応用です。どの種類も反対側へ移せます。',
  colorByKind: '種類ごとに色分け',
  help: '使い方',
  helpWheel: 'ホイール — 千年紀から日まで滑らかにズーム',
  helpDrag: 'ドラッグ — 時間を移動',
  helpCluster: '数字入りの円 — まとまり。クリックで拡大し、拡大できない場合は一覧を表示',
  helpHover: 'ホバー — 出来事のカード',
  above: '↑ 上',
  below: '↓ 下',
  loading: '読み込み中…',
  apiUnavailable: 'API に接続できません',
  eventsInView: (n) => plural(n, { other: '表示範囲に # 件' }),
  clusterTitle: (n) => plural(n, { other: '# 件の出来事' }),
  clusterHint: 'クリックでこの期間を拡大',
  clusterHintList: 'クリックで一覧を表示',
  listShowing: (shown, total) => `${total} 件中 ${shown} 件を表示`,
  listMore: 'もっと見る',
  close: '閉じる',
  bandLabel: '現在の縮尺より日付が大まかなもの',
  bandEmpty: 'ここには該当する出来事はありません',
  bandMore: (shown, hidden) => `${shown} 件表示、ほか ${hidden} 件`,
  support: '支援',
  supportHint: '役に立ったら支援できます。',
  sourceCode: 'ソースコード',
  dataPrefix: 'データ：',
  built: (date) => `ビルド ${date}`,
  disclaimer: 'データは自動収集のため、欠落や古い情報が含まれることがあります。原典をご確認ください。',
  reportIssue: '問題を報告',
  kinds: {
    discovery: '発見',
    observation: '観測',
    confirmation: '確認',
    publication: '論文',
    invention: '発明',
    refutation: '反証',
    award: '賞',
    other: 'その他',
  },
  categories: {
    physics: '物理学',
    chemistry: '化学',
    biology: '生物学',
    medicine: '医学',
    astronomy: '天文学',
    mathematics: '数学',
    computing: '計算機科学',
    earth: '地球科学',
    engineering: '工学',
    psychology: '心理学',
    social: '社会科学',
  },
  decade: (year, bc) => (bc ? `紀元前 ${year} 年代` : `${year} 年代`),
  century: (i, bc) => (bc ? `紀元前 ${i} 世紀` : `${i} 世紀`),
  millennium: (i, bc) => (bc ? `紀元前 ${i} 千年紀` : `${i} 千年紀`),
  circa: (label) => `約 ${label}`,
};

const ar: Dict = {
  ...en,
  appTitle: 'الخط الزمني لتاريخ العلوم',
  searchPlaceholder: 'ابحث عن اكتشاف أو اختراع أو عالم…',
  nothingFound: 'لا توجد نتائج',
  zoomIn: 'تكبير',
  zoomOut: 'تصغير',
  goToday: 'اليوم',
  wholeHistory: 'التاريخ كله',
  theme: 'المظهر',
  themeAuto: 'حسب النظام',
  themeLight: 'فاتح',
  themeDark: 'داكن',
  language: 'اللغة',
  fields: 'مجالات العلوم',
  kindsHeading: 'أنواع الأحداث',
  layout: 'التوزيع',
  layoutHint: 'فوق الخط المعرفة الجديدة، وتحته التغيير والتطبيق. يمكن نقل أي نوع إلى الجهة الأخرى.',
  colorByKind: 'التلوين حسب نوع الحدث',
  help: 'طريقة الاستخدام',
  helpWheel: 'عجلة الفأرة — تكبير متدرج من الألفيات إلى الأيام',
  helpDrag: 'السحب — التنقل عبر الزمن',
  helpCluster: 'دائرة بها رقم — تجمّع: انقر للتكبير، أو لعرض القائمة إذا لم يعد هناك ما يُكبّر',
  helpHover: 'التمرير — بطاقة الحدث',
  above: '↑ أعلى',
  below: '↓ أسفل',
  loading: 'جارٍ التحميل…',
  apiUnavailable: 'الواجهة البرمجية غير متاحة',
  eventsInView: (n) => plural(n, {
    one: 'حدث واحد في النطاق',
    two: 'حدثان في النطاق',
    few: '# أحداث في النطاق',
    many: '# حدثًا في النطاق',
    other: '# حدث في النطاق',
  }),
  clusterTitle: (n) => plural(n, {
    one: 'حدث واحد',
    two: 'حدثان',
    few: '# أحداث',
    many: '# حدثًا',
    other: '# حدث',
  }),
  clusterHint: 'انقر لتكبير هذه الفترة',
  clusterHintList: 'انقر لعرض القائمة',
  listShowing: (shown, total) => `عرض ${shown} من ${total}`,
  listMore: 'عرض المزيد',
  close: 'إغلاق',
  bandLabel: 'تاريخها أقل دقة من المقياس الحالي',
  bandEmpty: 'لا توجد أحداث من هذا النوع هنا',
  bandMore: (shown, hidden) => `معروض ${shown}، و${hidden} أخرى`,
  support: 'ادعم',
  supportHint: 'إذا كان مفيدًا، يمكنك المساهمة.',
  sourceCode: 'الشيفرة المصدرية',
  dataPrefix: 'البيانات:',
  built: (date) => `إصدار ${date}`,
  disclaimer: 'تُجمع البيانات آليًا وقد تكون ناقصة أو قديمة — راجع المصادر الأصلية.',
  reportIssue: 'الإبلاغ عن مشكلة',
  kinds: {
    discovery: 'اكتشافات',
    observation: 'أرصاد',
    confirmation: 'تأكيدات',
    publication: 'منشورات',
    invention: 'اختراعات',
    refutation: 'دحض',
    award: 'جوائز',
    other: 'أخرى',
  },
  categories: {
    physics: 'الفيزياء',
    chemistry: 'الكيمياء',
    biology: 'الأحياء',
    medicine: 'الطب',
    astronomy: 'علم الفلك',
    mathematics: 'الرياضيات',
    computing: 'علوم الحاسوب',
    earth: 'علوم الأرض',
    engineering: 'الهندسة',
    psychology: 'علم النفس',
    social: 'العلوم الاجتماعية',
  },
  decade: (year, bc) => (bc ? `عقد ${year} ق.م` : `عقد ${year}`),
  century: (i, bc) => (bc ? `القرن ${i} ق.م` : `القرن ${i}`),
  millennium: (i, bc) => (bc ? `الألفية ${i} ق.م` : `الألفية ${i}`),
  circa: (label) => `نحو ${label}`,
};

const hi: Dict = {
  ...en,
  appTitle: 'विज्ञान के इतिहास की समयरेखा',
  searchPlaceholder: 'कोई खोज, आविष्कार या वैज्ञानिक खोजें…',
  nothingFound: 'कुछ नहीं मिला',
  zoomIn: 'बड़ा करें',
  zoomOut: 'छोटा करें',
  goToday: 'आज',
  wholeHistory: 'पूरा इतिहास',
  theme: 'थीम',
  themeAuto: 'सिस्टम के अनुसार',
  themeLight: 'हल्की',
  themeDark: 'गहरी',
  language: 'भाषा',
  fields: 'विज्ञान के क्षेत्र',
  kindsHeading: 'घटना के प्रकार',
  layout: 'व्यवस्था',
  layoutHint: 'रेखा के ऊपर नया ज्ञान, नीचे परिवर्तन और उपयोग। किसी भी प्रकार को दूसरी ओर ले जाया जा सकता है।',
  colorByKind: 'घटना के प्रकार से रंग',
  help: 'उपयोग कैसे करें',
  helpWheel: 'माउस व्हील — सहस्राब्दियों से दिनों तक सहज ज़ूम',
  helpDrag: 'खींचें — समय में आगे-पीछे जाएँ',
  helpCluster: 'संख्या वाला घेरा — समूह: क्लिक करने पर बड़ा होगा, और बड़ा न हो सके तो सूची दिखेगी',
  helpHover: 'कर्सर ले जाएँ — घटना का कार्ड',
  above: '↑ ऊपर',
  below: '↓ नीचे',
  loading: 'लोड हो रहा है…',
  apiUnavailable: 'API उपलब्ध नहीं',
  eventsInView: (n) => plural(n, { one: 'दृश्य में # घटना', other: 'दृश्य में # घटनाएँ' }),
  clusterTitle: (n) => plural(n, { one: '# घटना', other: '# घटनाएँ' }),
  clusterHint: 'इस अवधि को बड़ा करने के लिए क्लिक करें',
  clusterHintList: 'सूची देखने के लिए क्लिक करें',
  listShowing: (shown, total) => `${total} में से ${shown} दिखाए गए`,
  listMore: 'और दिखाएँ',
  close: 'बंद करें',
  bandLabel: 'तिथि वर्तमान पैमाने से कम सटीक है',
  bandEmpty: 'यहाँ ऐसी कोई घटना नहीं है',
  bandMore: (shown, hidden) => `${shown} दिखाई गईं, ${hidden} और`,
  support: 'सहयोग करें',
  supportHint: 'अगर यह उपयोगी लगे तो आप सहयोग कर सकते हैं।',
  sourceCode: 'स्रोत कोड',
  dataPrefix: 'डेटा:',
  built: (date) => `संस्करण ${date}`,
  disclaimer: 'डेटा स्वतः एकत्र किया गया है और अधूरा या पुराना हो सकता है — मूल स्रोत देखें।',
  reportIssue: 'समस्या की सूचना दें',
  kinds: {
    discovery: 'खोजें',
    observation: 'प्रेक्षण',
    confirmation: 'पुष्टियाँ',
    publication: 'प्रकाशन',
    invention: 'आविष्कार',
    refutation: 'खंडन',
    award: 'पुरस्कार',
    other: 'अन्य',
  },
  categories: {
    physics: 'भौतिकी',
    chemistry: 'रसायन विज्ञान',
    biology: 'जीव विज्ञान',
    medicine: 'चिकित्सा',
    astronomy: 'खगोल विज्ञान',
    mathematics: 'गणित',
    computing: 'कंप्यूटर विज्ञान',
    earth: 'पृथ्वी विज्ञान',
    engineering: 'अभियांत्रिकी',
    psychology: 'मनोविज्ञान',
    social: 'सामाजिक विज्ञान',
  },
  decade: (year, bc) => (bc ? `${year} का दशक ईसा पूर्व` : `${year} का दशक`),
  century: (i, bc) => (bc ? `${i}वीं सदी ईसा पूर्व` : `${i}वीं सदी`),
  millennium: (i, bc) => (bc ? `${i}री सहस्राब्दी ईसा पूर्व` : `${i}री सहस्राब्दी`),
  circa: (label) => `लगभग ${label}`,
};

const DICTS: Record<LangCode, Dict> = { en, ru, es, fr, de, pt, zh, ja, ar, hi };

let current: LangCode = 'ru';

export function setLanguage(lang: LangCode): void {
  current = lang;
  document.documentElement.lang = lang;
  document.documentElement.dir = RTL_LANGUAGES.includes(lang) ? 'rtl' : 'ltr';
}

export const t = (): Dict => DICTS[current];
export const currentLanguage = (): LangCode => current;

/** Язык браузера, если он есть среди поддерживаемых. */
export function detectLanguage(): LangCode {
  for (const tag of navigator.languages ?? [navigator.language]) {
    const base = tag.toLowerCase().split('-')[0] as LangCode;
    if (base in DICTS) return base;
  }
  return 'en';
}

// ---------------------------------------------------------------------
// Подписи дат
// ---------------------------------------------------------------------

/**
 * Дата из астрономического года. Конструктор Date трактует годы 0–99
 * как 1900-е, поэтому год выставляется отдельным вызовом.
 */
function utcDate(year: number, month: number, day: number): Date {
  const date = new Date(0);
  date.setUTCFullYear(year, month - 1, day);
  date.setUTCHours(0, 0, 0, 0);
  return date;
}

const formatterCache = new Map<string, Intl.DateTimeFormat>();

function formatter(lang: string, options: Intl.DateTimeFormatOptions): Intl.DateTimeFormat {
  const key = lang + JSON.stringify(options);
  let value = formatterCache.get(key);
  if (!value) {
    value = new Intl.DateTimeFormat(lang, { ...options, timeZone: 'UTC' });
    formatterCache.set(key, value);
  }
  return value;
}

/**
 * Границы века или тысячелетия по историческому счёту, где нулевого года нет:
 * XX век — это 1901–2000, а I век до н. э. заканчивается 1 годом н. э.
 * Повторяет TimeLabel.UnitRange на сервере.
 */
function unitIndex(astronomicalYear: number, unitSize: number): { index: number; bc: boolean } {
  const l = astronomicalYear > 0 ? astronomicalYear : astronomicalYear - 1;
  const bc = l < 0;
  const magnitude = bc ? -l : l;

  return { index: Math.floor((magnitude - 1) / unitSize) + 1, bc };
}

/** Подпись даты по интервалу на оси и точности датировки. */
export function formatDate(tStart: number, precision: string, circa = false): string {
  const dict = t();
  const lang = current;
  const { year, month, day } = toGregorian(tStart);
  const bc = year <= 0;

  let label: string;

  switch (precision) {
    case 'day':
      label = formatter(lang, { year: 'numeric', month: 'long', day: 'numeric', era: bc ? 'short' : undefined })
        .format(utcDate(year, month, day));
      break;

    case 'month':
      label = formatter(lang, { year: 'numeric', month: 'long', era: bc ? 'short' : undefined })
        .format(utcDate(year, month, 1));
      break;

    case 'year':
      label = formatter(lang, { year: 'numeric', era: bc ? 'short' : undefined })
        .format(utcDate(year, 1, 1));
      break;

    case 'decade': {
      const start = Math.floor(year / 10) * 10;
      label = dict.decade(start <= 0 ? 1 - start : start, start <= 0);
      break;
    }

    case 'century': {
      const { index, bc: isBc } = unitIndex(year, 100);
      label = dict.century(index, isBc);
      break;
    }

    case 'millennium': {
      const { index, bc: isBc } = unitIndex(year, 1000);
      label = dict.millennium(index, isBc);
      break;
    }

    default:
      return '';
  }

  return circa ? dict.circa(label) : label;
}

/**
 * Подпись засечки шкалы. Короткая: на оси помещается немного,
 * а год повторяется в каждой засечке только там, где он меняется.
 */
export function formatTick(tick: Tick): string {
  const lang = current;
  const bc = tick.year <= 0;
  const date = utcDate(tick.year, tick.month, tick.day);

  switch (tick.unit) {
    case 'day':
      return formatter(lang, { day: 'numeric', month: 'short' }).format(date);

    case 'month':
      return formatter(lang, { month: 'short', year: bc ? 'numeric' : undefined, era: bc ? 'short' : undefined })
        .format(date);

    default:
      return formatter(lang, { year: 'numeric', era: bc ? 'short' : undefined }).format(date);
  }
}

/** Заголовок видимого диапазона в шапке. */
export function formatRange(lo: number, hi: number): string {
  const unit = zoomUnit(hi - lo);
  const precision = unit === 'day' || unit === 'month' ? 'day' : 'year';

  return `${formatDate(lo, precision)} — ${formatDate(hi, precision)}`;
}
