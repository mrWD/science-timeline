-- Области науки. Цвета подобраны так, чтобы различаться и на светлом,
-- и на тёмном фоне ленты. Идемпотентно — можно применять повторно.

insert into categories (slug, name_ru, name_en, color) values
    ('physics',      'Физика',            'Physics',          '#4C8DFF'),
    ('chemistry',    'Химия',             'Chemistry',        '#22B07D'),
    ('biology',      'Биология',          'Biology',          '#6BC24A'),
    ('medicine',     'Медицина',          'Medicine',         '#E5484D'),
    ('astronomy',    'Астрономия',        'Astronomy',        '#8E6BF0'),
    ('mathematics',  'Математика',        'Mathematics',      '#F0A020'),
    ('computing',    'Информатика',       'Computer science', '#00A5B5'),
    ('earth',        'Науки о Земле',     'Earth science',    '#A5713A'),
    ('engineering',  'Техника',           'Engineering',      '#DA6C2E'),
    ('psychology',   'Психология',        'Psychology',       '#D6489B'),
    ('social',       'Общественные науки','Social science',   '#7A8794')
on conflict (slug) do update
    set name_ru = excluded.name_ru,
        name_en = excluded.name_en,
        color   = excluded.color;
